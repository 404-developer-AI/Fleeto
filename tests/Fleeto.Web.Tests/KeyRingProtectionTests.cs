using System.Security.Cryptography;
using Fleeto.Core.Interfaces;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Web.Tests;

/// <summary>
/// The data protection key ring of web is sealed with the root key (0.6.0): no key on the volume is readable without it, a
/// restarted web still reads its own keys, and a key stored unencrypted before 0.6.0 is retired at start, so nothing it
/// protected is accepted any more.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class KeyRingProtectionTests : IDisposable
{
    private const string Purpose = "Fleeto.Tests.KeyRing";

    private readonly WebFixture _fixture;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("fleeto-keyring-");

    public KeyRingProtectionTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>A web as Program.cs builds it; <paramref name="sealed"/> false is web before 0.6.0.</summary>
    private ServiceProvider Web(bool @sealed = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProtector>(_fixture.Database.SecretProtector);
        var dataProtection = services.AddDataProtection().PersistKeysToFileSystem(_directory).SetApplicationName("Fleeto.Web");
        if (@sealed)
        {
            dataProtection.ProtectKeysWithRootKey();
        }

        return services.BuildServiceProvider();
    }

    private string KeyFiles() => string.Concat(_directory.EnumerateFiles("key-*.xml").Select(f => File.ReadAllText(f.FullName)));

    [Fact]
    public void Keys_are_sealed_with_the_root_key_and_read_again_after_a_restart()
    {
        string payload;
        using (var web = Web())
        {
            payload = web.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("session");
        }

        var files = KeyFiles();
        Assert.Contains(KeyRingProtection.EncryptedElementName, files);
        Assert.DoesNotContain("masterKey", files);
        Assert.DoesNotContain("requiresEncryption", files);

        using var restarted = Web();
        Assert.Equal(0, KeyRingProtection.RetireUnencryptedKeys(restarted.GetRequiredService<IKeyManager>(), _directory, NullLogger.Instance));
        Assert.Equal("session", restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(payload));
    }

    [Fact]
    public void A_key_stored_unencrypted_before_0_6_0_is_retired_at_start()
    {
        string oldPayload;
        using (var before = Web(@sealed: false))
        {
            oldPayload = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("old session");
        }

        Assert.Contains("masterKey", KeyFiles());

        using var after = Web();
        Assert.Equal(1, KeyRingProtection.RetireUnencryptedKeys(after.GetRequiredService<IKeyManager>(), _directory, NullLogger.Instance));
        Assert.DoesNotContain("masterKey", KeyFiles());

        // What the unencrypted key protected is no longer accepted: that session signs in again.
        var protector = after.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(oldPayload));

        // The key that replaces it is sealed.
        Assert.Equal("new session", protector.Unprotect(protector.Protect("new session")));
        Assert.Contains(KeyRingProtection.EncryptedElementName, KeyFiles());
        Assert.Equal(0, KeyRingProtection.RetireUnencryptedKeys(after.GetRequiredService<IKeyManager>(), _directory, NullLogger.Instance));
    }
}
