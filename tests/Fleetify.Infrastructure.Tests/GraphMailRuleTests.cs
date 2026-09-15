using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fleetify.Core.Domain;
using Fleetify.Infrastructure.Email;
using Fleetify.Infrastructure.Settings;

namespace Fleetify.Infrastructure.Tests;

/// <summary>
/// Microsoft Graph email and expiring credentials (0.2.0): warning stages at 30, 14, 7 and 1 days and after expiry, only the
/// most urgent skipped stage is sent; the certificate Fleeto creates is RSA with a two-year lifetime; the client assertion is a
/// valid RS256 JWT for the token endpoint bound to the certificate; tenant and client IDs are checked.
/// </summary>
public class GraphMailRuleTests
{
    private static readonly DateTime Expiry = new(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    [Theory]
    [InlineData(45, CredentialExpiryStage.None)]
    [InlineData(30, CredentialExpiryStage.Days30)]
    [InlineData(15, CredentialExpiryStage.Days30)]
    [InlineData(14, CredentialExpiryStage.Days14)]
    [InlineData(8, CredentialExpiryStage.Days14)]
    [InlineData(7, CredentialExpiryStage.Days7)]
    [InlineData(2, CredentialExpiryStage.Days7)]
    [InlineData(1, CredentialExpiryStage.Day1)]
    [InlineData(0, CredentialExpiryStage.Expired)]
    [InlineData(-10, CredentialExpiryStage.Expired)]
    public void Stages_follow_the_warning_days(int daysBefore, CredentialExpiryStage expected)
    {
        Assert.Equal(expected, CredentialExpiryRules.StageFor(Expiry, Expiry.AddDays(-daysBefore)));
    }

    [Fact]
    public void A_warning_is_needed_only_for_a_more_urgent_stage()
    {
        Assert.True(CredentialExpiryRules.NeedsWarning(CredentialExpiryStage.Days30, CredentialExpiryStage.None));
        Assert.False(CredentialExpiryRules.NeedsWarning(CredentialExpiryStage.Days30, CredentialExpiryStage.Days30));
        Assert.True(CredentialExpiryRules.NeedsWarning(CredentialExpiryStage.Expired, CredentialExpiryStage.Days30));
        Assert.False(CredentialExpiryRules.NeedsWarning(CredentialExpiryStage.Days7, CredentialExpiryStage.Expired));
        Assert.False(CredentialExpiryRules.NeedsWarning(CredentialExpiryStage.None, CredentialExpiryStage.None));
    }

    [Fact]
    public void The_credential_in_use_decides_the_expiry()
    {
        var secret = new GraphMailSettings
        {
            TenantId = "contoso.onmicrosoft.com", ClientId = Guid.NewGuid().ToString(), SenderAddress = "fleeto@contoso.com",
            ClientSecret = "s", ClientSecretExpiresAt = Expiry, CertificateExpiresAt = Expiry.AddYears(1)
        };
        Assert.Equal(Expiry, secret.CredentialExpiresAt);
        Assert.True(secret.IsComplete);

        var certificateWithoutOne = secret with { CredentialType = GraphCredentialType.Certificate };
        Assert.Null(certificateWithoutOne.CredentialExpiresAt);
        Assert.False(certificateWithoutOne.IsComplete);
        Assert.False((secret with { SenderAddress = "" }).IsComplete);
    }

    [Theory]
    [InlineData("0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d", true)]
    [InlineData("contoso.onmicrosoft.com", true)]
    [InlineData("contoso", false)]
    [InlineData("contoso.com/../x", false)]
    [InlineData("", false)]
    public void Tenant_ids_are_guids_or_domains(string value, bool valid)
    {
        Assert.Equal(valid, GraphMail.IsValidTenantId(value));
    }

    [Fact]
    public void The_certificate_is_rsa_for_two_years_and_the_assertion_is_signed_with_it()
    {
        var now = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
        var certificate = GraphMail.CreateCertificate("rmm.example.com", now);

        using var loaded = GraphMail.LoadCertificate(certificate.PfxBase64);
        Assert.NotNull(loaded.GetRSAPrivateKey());
        Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
        Assert.InRange(certificate.ExpiresAt, now.AddDays(729), now.AddDays(731));
        Assert.Contains("rmm.example.com", loaded.Subject);

        using var publicOnly = X509CertificateLoader.LoadCertificate(GraphMail.PublicCertificate(certificate.PfxBase64));
        Assert.False(publicOnly.HasPrivateKey);

        var clientId = Guid.NewGuid().ToString();
        var assertion = GraphMail.ClientAssertion(certificate.PfxBase64, "contoso.onmicrosoft.com", clientId, new DateTimeOffset(now));
        var parts = assertion.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(Base64Url(publicOnly.GetCertHash(HashAlgorithmName.SHA256)), header.RootElement.GetProperty("x5t#S256").GetString());

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        Assert.Equal("https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token", payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal(clientId, payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(clientId, payload.RootElement.GetProperty("sub").GetString());
        Assert.Equal(new DateTimeOffset(now).ToUnixTimeSeconds() + 600, payload.RootElement.GetProperty("exp").GetInt64());

        using var rsa = publicOnly.GetRSAPublicKey()!;
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
