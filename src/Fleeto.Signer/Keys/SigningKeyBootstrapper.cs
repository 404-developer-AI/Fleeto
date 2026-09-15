using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Fleeto.Signer.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Signer.Keys;

/// <summary>
/// Thrown when the key material in the database cannot be used with the loaded signer key. The signer must stop:
/// generating new keys would silently break every enrolled agent, which pinned the existing CA and signing key.
/// </summary>
public sealed class SignerKeyMismatchException : Exception
{
    public SignerKeyMismatchException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Makes sure the instance has exactly one active instance signing key (ed25519) and one active internal CA
/// (ECDSA P-256), both encrypted under the signer key, and loads them into the <see cref="SignerKeyRing"/>.
/// Creation happens only when no key of that kind exists; existing keys that do not open with the loaded signer
/// key stop the signer instead of being replaced.
/// </summary>
public sealed class SigningKeyBootstrapper
{
    public const string MismatchMessage =
        "The signer key does not belong to this instance's database. Restore the correct signer.key; " +
        "generating new keys would break every enrolled agent.";

    /// <summary>Audit action for a newly created instance signing key.</summary>
    public const string SigningKeyCreatedAction = "signing_key.created";

    /// <summary>Audit action for a newly created internal CA.</summary>
    public const string CertificateAuthorityCreatedAction = "certificate_authority.created";

    /// <summary>
    /// Advisory lock key for key creation, so two signer processes starting together cannot both create keys.
    /// Any constant works; "FlSigner" in ASCII.
    /// </summary>
    private const long KeyCreationLockKey = 0x466C5369676E6572;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SignerKey _signerKey;
    private readonly SignerKeyRing _keyRing;
    private readonly TimeProvider _time;
    private readonly ILogger<SigningKeyBootstrapper> _logger;

    public SigningKeyBootstrapper(IFleetoDbContextFactory dbFactory, SignerKey signerKey, SignerKeyRing keyRing, TimeProvider time,
        ILogger<SigningKeyBootstrapper> logger)
    {
        _dbFactory = dbFactory;
        _signerKey = signerKey;
        _keyRing = keyRing;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Ensures and loads the keys. Throws <see cref="SignerKeyMismatchException"/> when existing keys cannot be opened
    /// or are inconsistent; nothing is created in that case.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({KeyCreationLockKey})", cancellationToken);

        var instance = await db.InstanceSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The instance is not initialised. Run fleeto-tool migrate first.");
        var now = _time.GetUtcNow().UtcDateTime;

        var signingKeys = await db.InstanceSigningKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);
        var authorities = await db.CertificateAuthorities.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(cancellationToken);

        // Open everything that exists before creating anything, so a wrong signer key never leads to new keys.
        var signing = OpenSigningKey(signingKeys);
        OpenedCa? ca;
        try
        {
            ca = OpenCertificateAuthority(authorities);
        }
        catch
        {
            if (signing is not null)
            {
                CryptographicOperations.ZeroMemory(signing.Seed);
            }

            throw;
        }

        if (signing is null)
        {
            var (seed, publicKey) = Ed25519.GenerateKeyPair();
            var id = KeyIds.For(publicKey);
            db.InstanceSigningKeys.Add(new InstanceSigningKey
            {
                Id = id,
                PublicKey = publicKey,
                EncryptedPrivateKey = _signerKey.Seal(seed, "InstanceSigningKeys|" + id),
                CreatedAt = now
            });
            await SignerAudit.WriteAsync(db, new AuditRecord(SigningKeyCreatedAction, "InstanceSigningKey", id, null,
                AuditActorType.System, "fleeto-signer", "fleeto-signer", new { KeyId = id }), now, cancellationToken);
            signing = new OpenedSigningKey(id, publicKey, seed);
            _logger.LogWarning("Created the instance signing key {KeyId}. Include it in the key ceremony backup", id);
        }

        if (ca is null)
        {
            var material = InternalCertificateAuthority.CreateCa(instance.Fqdn, now);
            var id = Guid.NewGuid();
            db.CertificateAuthorities.Add(new CertificateAuthority
            {
                Id = id,
                CertificateDer = material.CertificateDer,
                Fingerprint = material.Fingerprint,
                EncryptedPrivateKey = _signerKey.Seal(material.PrivateKeyPkcs8, "CertificateAuthorities|" + id.ToString("D")),
                CreatedAt = now,
                ExpiresAt = material.ExpiresAt
            });
            await SignerAudit.WriteAsync(db, new AuditRecord(CertificateAuthorityCreatedAction, "CertificateAuthority", id.ToString(), null,
                AuditActorType.System, "fleeto-signer", "fleeto-signer", new { material.Fingerprint, material.ExpiresAt }), now, cancellationToken);
            ca = new OpenedCa(id, material.CertificateDer, material.PrivateKeyPkcs8, material.ExpiresAt);
            _logger.LogWarning("Created the internal CA {CaId} with fingerprint {Fingerprint}", id, material.Fingerprint);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (ca.ExpiresAt < now.AddDays(365))
        {
            _logger.LogWarning("The internal CA {CaId} expires on {ExpiresAt:yyyy-MM-dd}. Plan a CA rotation before then", ca.Id, ca.ExpiresAt);
        }

        _keyRing.Load(instance.InstanceId, signing.Id, signing.PublicKey, signing.Seed, ca.Id, ca.CertificateDer, ca.PrivateKeyPkcs8);
        _logger.LogInformation("Signer keys loaded: signing key {KeyId}, CA {CaId}, signer key {SignerKeyId}", signing.Id, ca.Id, _signerKey.Id);
    }

    private sealed record OpenedSigningKey(string Id, byte[] PublicKey, byte[] Seed);

    private sealed record OpenedCa(Guid Id, byte[] CertificateDer, byte[] PrivateKeyPkcs8, DateTime ExpiresAt);

    /// <summary>
    /// Opens the active signing key. Returns null only when no signing key row exists at all. When every row is
    /// retired, the newest one is still opened to prove the signer key belongs to this database, and a missing
    /// active key is an error rather than a reason to generate one.
    /// </summary>
    private OpenedSigningKey? OpenSigningKey(IReadOnlyList<InstanceSigningKey> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var active = rows.Where(k => k.RetiredAt is null).ToList();
        var probe = active.Count > 0 ? active[0] : rows[0];

        byte[] seed;
        try
        {
            seed = _signerKey.Open(probe.EncryptedPrivateKey, "InstanceSigningKeys|" + probe.Id);
        }
        catch (CryptographicException ex)
        {
            throw new SignerKeyMismatchException(MismatchMessage, ex);
        }

        if (seed.Length != Ed25519.PrivateKeySize ||
            !CryptographicOperations.FixedTimeEquals(Ed25519.PublicKeyFromPrivate(seed), probe.PublicKey) ||
            KeyIds.For(probe.PublicKey) != probe.Id)
        {
            CryptographicOperations.ZeroMemory(seed);
            throw new SignerKeyMismatchException(
                $"The instance signing key {probe.Id} is inconsistent: its private key does not match its public key or id. Restore the database from a backup.");
        }

        if (active.Count == 0)
        {
            CryptographicOperations.ZeroMemory(seed);
            throw new SignerKeyMismatchException(
                "Every instance signing key is retired and no active key exists. Restore the database from a backup; generating a new key would break every enrolled agent.");
        }

        if (active.Count > 1)
        {
            CryptographicOperations.ZeroMemory(seed);
            throw new SignerKeyMismatchException(
                $"{active.Count} instance signing keys are active; exactly one is allowed. Retire the keys that agents did not pin, then start the signer again.");
        }

        return new OpenedSigningKey(probe.Id, probe.PublicKey, seed);
    }

    private OpenedCa? OpenCertificateAuthority(IReadOnlyList<CertificateAuthority> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var active = rows.Where(c => c.RetiredAt is null).ToList();
        var probe = active.Count > 0 ? active[0] : rows[0];

        byte[] pkcs8;
        try
        {
            pkcs8 = _signerKey.Open(probe.EncryptedPrivateKey, "CertificateAuthorities|" + probe.Id.ToString("D"));
        }
        catch (CryptographicException ex)
        {
            throw new SignerKeyMismatchException(MismatchMessage, ex);
        }

        if (!CaKeyMatchesCertificate(probe.CertificateDer, pkcs8) || KeyIds.Sha256Hex(probe.CertificateDer) != probe.Fingerprint)
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            throw new SignerKeyMismatchException(
                $"The internal CA {probe.Id} is inconsistent: its private key does not match its certificate. Restore the database from a backup.");
        }

        if (active.Count != 1)
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            throw new SignerKeyMismatchException(active.Count == 0
                ? "Every internal CA is retired and no active CA exists. Restore the database from a backup; a new CA would break every enrolled agent."
                : $"{active.Count} internal CAs are active; exactly one is allowed. Retire the CA that agents did not pin, then start the signer again.");
        }

        return new OpenedCa(probe.Id, probe.CertificateDer, pkcs8, probe.ExpiresAt);
    }

    private static bool CaKeyMatchesCertificate(byte[] certificateDer, byte[] pkcs8)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
            using var certificateKey = certificate.GetECDsaPublicKey();
            return certificateKey is not null &&
                   key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(certificateKey.ExportSubjectPublicKeyInfo());
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
