using System.Security.Cryptography;
using Fleetify.Infrastructure.Security;
using Fleetify.Signer.Keys;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Signer.Tests;

/// <summary>
/// Guarantees that the instance signing key and the internal CA are created once, reused on every start, and never
/// replaced when the signer key does not belong to the database.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class SigningKeyBootstrapperTests
{
    private readonly SignerFixture _fixture;

    public SigningKeyBootstrapperTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Bootstrap_creates_one_signing_key_and_one_ca_and_reuses_them_on_every_start()
    {
        using var firstRestart = new SignerKeyRing();
        using var secondRestart = new SignerKeyRing();
        await _fixture.CreateBootstrapper(_fixture.Database.SignerKey, firstRestart).RunAsync();
        await _fixture.CreateBootstrapper(_fixture.Database.SignerKey, secondRestart).RunAsync();

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var signingKey = Assert.Single(await db.InstanceSigningKeys.AsNoTracking().ToListAsync());
        var ca = Assert.Single(await db.CertificateAuthorities.AsNoTracking().ToListAsync());

        Assert.Equal(_fixture.KeyRing.SigningKeyId, signingKey.Id);
        Assert.Equal(signingKey.Id, firstRestart.SigningKeyId);
        Assert.Equal(signingKey.Id, secondRestart.SigningKeyId);
        Assert.Equal(KeyIds.For(signingKey.PublicKey), signingKey.Id);
        Assert.Equal(signingKey.PublicKey, secondRestart.SigningPublicKey);
        Assert.Equal(ca.Id, secondRestart.CaId);
        Assert.Equal(ca.CertificateDer, secondRestart.CaCertificateDer);
        Assert.Equal(KeyIds.Sha256Hex(ca.CertificateDer), ca.Fingerprint);
        Assert.Equal(_fixture.Database.InstanceId, secondRestart.InstanceId);

        // The stored private key is not the plain seed, and opens only with the signer key and its row context.
        var seed = _fixture.Database.SignerKey.Open(signingKey.EncryptedPrivateKey, "InstanceSigningKeys|" + signingKey.Id);
        Assert.Equal(signingKey.PublicKey, Ed25519.PublicKeyFromPrivate(seed));
        Assert.ThrowsAny<CryptographicException>(() =>
            _fixture.Database.SignerKey.Open(signingKey.EncryptedPrivateKey, "InstanceSigningKeys|other"));
    }

    [Fact]
    public async Task Bootstrap_with_a_signer_key_of_another_instance_fails_and_creates_nothing()
    {
        await using var before = _fixture.Database.DbFactory.CreateSystem();
        var signingKeysBefore = await before.InstanceSigningKeys.CountAsync();
        var authoritiesBefore = await before.CertificateAuthorities.CountAsync();
        var auditBefore = await before.AuditEntries.CountAsync();

        using var keyRing = new SignerKeyRing();
        var wrongKey = new SignerKey(RandomNumberGenerator.GetBytes(32));
        var exception = await Assert.ThrowsAsync<SignerKeyMismatchException>(() =>
            _fixture.CreateBootstrapper(wrongKey, keyRing).RunAsync());

        Assert.Equal(SigningKeyBootstrapper.MismatchMessage, exception.Message);
        Assert.False(keyRing.IsLoaded);

        await using var after = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(signingKeysBefore, await after.InstanceSigningKeys.CountAsync());
        Assert.Equal(authoritiesBefore, await after.CertificateAuthorities.CountAsync());
        Assert.Equal(auditBefore, await after.AuditEntries.CountAsync());
    }
}
