using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fleeto.Infrastructure.Email;

/// <summary>A certificate for the Graph app registration: the private key stays in the instance, encrypted.</summary>
public sealed record GraphCertificate(string PfxBase64, string Thumbprint, DateTime ExpiresAt);

/// <summary>
/// Microsoft Graph email rules shared by web (settings, certificate) and workers (delivery): endpoints, input checks, the
/// certificate Fleeto creates, and the signed client assertion that proves possession of its key (RFC 7523).
/// </summary>
public static partial class GraphMail
{
    public const string Scope = "https://graph.microsoft.com/.default";

    /// <summary>Lifetime of a certificate created by Fleeto.</summary>
    public static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(730);

    public static Uri TokenEndpoint(string tenantId) => new($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token");

    public static Uri SendMailEndpoint(string senderAddress) => new($"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(senderAddress)}/sendMail");

    /// <summary>A directory (tenant) ID or a verified domain such as contoso.onmicrosoft.com.</summary>
    public static bool IsValidTenantId(string? value) =>
        value is { Length: > 0 and <= 255 } && (Guid.TryParse(value, out _) || TenantDomain().IsMatch(value));

    public static bool IsValidClientId(string? value) => Guid.TryParse(value, out _);

    [GeneratedRegex(@"^(?=.{1,255}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$")]
    private static partial Regex TenantDomain();

    /// <summary>A new self-signed RSA certificate (Entra ID accepts RSA only) valid for <see cref="CertificateLifetime"/>.</summary>
    public static GraphCertificate CreateCertificate(string instanceFqdn, DateTime now)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName($"CN=Fleeto email {instanceFqdn}"), rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var notBefore = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).AddMinutes(-5);
        var notAfter = notBefore.Add(CertificateLifetime);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return new GraphCertificate(Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12)), certificate.Thumbprint,
            certificate.NotAfter.ToUniversalTime());
    }

    public static X509Certificate2 LoadCertificate(string pfxBase64) =>
        X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pfxBase64), null, X509KeyStorageFlags.EphemeralKeySet);

    /// <summary>The public certificate (DER) to upload to the app registration.</summary>
    public static byte[] PublicCertificate(string pfxBase64)
    {
        using var certificate = LoadCertificate(pfxBase64);
        return certificate.Export(X509ContentType.Cert);
    }

    /// <summary>A client assertion JWT (RS256) for the token request, valid for ten minutes.</summary>
    public static string ClientAssertion(string pfxBase64, string tenantId, string clientId, DateTimeOffset now)
    {
        using var certificate = LoadCertificate(pfxBase64);
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("The certificate has no RSA private key.");
        var header = new JsonObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = Base64Url(certificate.GetCertHash()),
            ["x5t#S256"] = Base64Url(certificate.GetCertHash(HashAlgorithmName.SHA256))
        };
        var seconds = now.ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["aud"] = TokenEndpoint(tenantId).AbsoluteUri,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["nbf"] = seconds - 60,
            ["iat"] = seconds,
            ["exp"] = seconds + 600
        };
        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
