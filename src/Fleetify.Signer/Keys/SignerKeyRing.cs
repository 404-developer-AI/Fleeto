using System.Security.Cryptography;
using Fleetify.Infrastructure.Security;

namespace Fleetify.Signer.Keys;

/// <summary>
/// The decrypted instance signing key and internal CA key, held in memory only. Raw key bytes never leave this
/// class: callers ask it to sign or issue. The arrays are zeroed when the host disposes the ring on shutdown.
/// </summary>
public sealed class SignerKeyRing : IDisposable
{
    private readonly Lock _lock = new();
    private Material? _material;

    private sealed record Material(
        Guid InstanceId,
        string SigningKeyId,
        byte[] SigningPublicKey,
        byte[] SigningSeed,
        Guid CaId,
        byte[] CaCertificateDer,
        byte[] CaPrivateKeyPkcs8);

    public bool IsLoaded => _material is not null;

    public Guid InstanceId => Current.InstanceId;
    public string SigningKeyId => Current.SigningKeyId;

    /// <summary>Raw 32-byte ed25519 public key; public by nature, returned as a copy.</summary>
    public byte[] SigningPublicKey => (byte[])Current.SigningPublicKey.Clone();

    public Guid CaId => Current.CaId;

    /// <summary>CA certificate DER; public by nature, returned as a copy.</summary>
    public byte[] CaCertificateDer => (byte[])Current.CaCertificateDer.Clone();

    private Material Current => _material
        ?? throw new InvalidOperationException("The signer keys are not loaded yet.");

    /// <summary>Installs freshly opened key material. The ring takes ownership of the arrays.</summary>
    internal void Load(Guid instanceId, string signingKeyId, byte[] signingPublicKey, byte[] signingSeed, Guid caId,
        byte[] caCertificateDer, byte[] caPrivateKeyPkcs8)
    {
        lock (_lock)
        {
            var previous = _material;
            _material = new Material(instanceId, signingKeyId, signingPublicKey, signingSeed, caId, caCertificateDer, caPrivateKeyPkcs8);
            if (previous is not null && !ReferenceEquals(previous.SigningSeed, signingSeed))
            {
                Zero(previous);
            }
        }
    }

    /// <summary>ed25519 signature over <c>context || 0x00 || payload</c> with the instance signing key.</summary>
    public byte[] Sign(string context, ReadOnlySpan<byte> payload)
    {
        var material = Current;
        var signature = Ed25519.Sign(material.SigningSeed, context, payload);

        // Why verify our own signature: a fault during signing (memory corruption, a bad CPU) can leak the key or
        // hand agents a signature they refuse. Verification costs microseconds.
        if (!Ed25519.Verify(material.SigningPublicKey, context, payload, signature))
        {
            throw new CryptographicException("A freshly made signature does not verify with the instance signing public key.");
        }

        return signature;
    }

    public InternalCertificateAuthority.IssuedCertificate IssueAgentCertificate(byte[] csrDer, Guid endpointId, DateTime now)
    {
        var material = Current;
        return InternalCertificateAuthority.IssueAgentCertificate(material.CaCertificateDer, material.CaPrivateKeyPkcs8, csrDer,
            endpointId, material.InstanceId, now);
    }

    public InternalCertificateAuthority.IssuedCertificate IssueGatewayCertificate(byte[] csrDer, IEnumerable<string> dnsNames, DateTime now)
    {
        var material = Current;
        return InternalCertificateAuthority.IssueGatewayCertificate(material.CaCertificateDer, material.CaPrivateKeyPkcs8, csrDer,
            dnsNames, now);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_material is not null)
            {
                Zero(_material);
                _material = null;
            }
        }
    }

    private static void Zero(Material material)
    {
        CryptographicOperations.ZeroMemory(material.SigningSeed);
        CryptographicOperations.ZeroMemory(material.CaPrivateKeyPkcs8);
    }
}
