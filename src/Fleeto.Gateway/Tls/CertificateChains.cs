using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Fleeto.Gateway.Tls;

/// <summary>
/// Chain validation against the instance CA only: custom root trust (no system roots), no revocation checks (the
/// allow list is the revocation mechanism) and no certificate downloads.
/// </summary>
internal static class CertificateChains
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>A chain policy for TLS: SslStream builds the client chain with it during the handshake.</summary>
    public static X509ChainPolicy CreatePolicy(X509Certificate2Collection trustedCas, string requiredEku)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        policy.CustomTrustStore.AddRange(trustedCas);
        policy.ApplicationPolicy.Add(new Oid(requiredEku));
        return policy;
    }

    /// <summary>
    /// Builds the chain of <paramref name="certificate"/> against <paramref name="trustedCas"/>. Returns the CA
    /// certificates of the chain (without the leaf) on success, null otherwise.
    /// </summary>
    public static X509Certificate2Collection? Build(X509Certificate2 certificate, X509Certificate2Collection trustedCas,
        string requiredEku, DateTime now)
    {
        if (trustedCas.Count == 0)
        {
            return null;
        }

        using var chain = new X509Chain { ChainPolicy = CreatePolicy(trustedCas, requiredEku) };
        chain.ChainPolicy.VerificationTime = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        chain.ChainPolicy.VerificationTimeIgnored = false;
        try
        {
            if (!chain.Build(certificate))
            {
                return null;
            }

            var issuers = new X509Certificate2Collection();
            for (var i = 1; i < chain.ChainElements.Count; i++)
            {
                issuers.Add(X509CertificateLoader.LoadCertificate(chain.ChainElements[i].Certificate.RawData));
            }

            return issuers;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }
        }
    }
}
