using System.Security.Cryptography.X509Certificates;
using Fleeto.Infrastructure.Identity;

namespace Fleeto.Infrastructure.Email;

/// <summary>A certificate for the Graph app registration: the private key stays in the instance, encrypted.</summary>
public sealed record GraphCertificate(string PfxBase64, string Thumbprint, DateTime ExpiresAt);

/// <summary>
/// Microsoft Graph email rules shared by web (settings, certificate) and workers (delivery). What every Entra ID app
/// registration shares — endpoints of a tenant, input checks, the certificate Fleeto creates and the signed client
/// assertion — lives in <see cref="MicrosoftIdentity"/>; this class is the email side of it.
/// </summary>
public static class GraphMail
{
    public const string Scope = "https://graph.microsoft.com/.default";

    /// <summary>Lifetime of a certificate created by Fleeto.</summary>
    public static readonly TimeSpan CertificateLifetime = MicrosoftIdentity.CertificateLifetime;

    public static Uri TokenEndpoint(string tenantId) => MicrosoftIdentity.TokenEndpoint(tenantId);

    public static Uri SendMailEndpoint(string senderAddress) => new($"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(senderAddress)}/sendMail");

    /// <summary>A directory (tenant) ID or a verified domain such as contoso.onmicrosoft.com.</summary>
    public static bool IsValidTenantId(string? value) => MicrosoftIdentity.IsValidTenantId(value);

    public static bool IsValidClientId(string? value) => MicrosoftIdentity.IsValidClientId(value);

    /// <summary>A new self-signed RSA certificate (Entra ID accepts RSA only) valid for <see cref="CertificateLifetime"/>.</summary>
    public static GraphCertificate CreateCertificate(string instanceFqdn, DateTime now)
    {
        var certificate = MicrosoftIdentity.CreateCertificate($"Fleeto email {instanceFqdn}", now);
        return new GraphCertificate(certificate.PfxBase64, certificate.Thumbprint, certificate.ExpiresAt);
    }

    public static X509Certificate2 LoadCertificate(string pfxBase64) => MicrosoftIdentity.LoadCertificate(pfxBase64);

    /// <summary>The public certificate (DER) to upload to the app registration.</summary>
    public static byte[] PublicCertificate(string pfxBase64) => MicrosoftIdentity.PublicCertificate(pfxBase64);

    /// <summary>A client assertion JWT (RS256) for the token request, valid for ten minutes.</summary>
    public static string ClientAssertion(string pfxBase64, string tenantId, string clientId, DateTimeOffset now) =>
        MicrosoftIdentity.ClientAssertion(pfxBase64, tenantId, clientId, now);
}
