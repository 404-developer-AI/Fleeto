using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Fleetify.Gateway.Tls;

/// <summary>
/// TLS settings for the agent port, chosen per handshake. A handshake callback rather than Kestrel's
/// <c>ServerCertificateSelector</c>, because the agent has only the CA fingerprint at enrollment and therefore needs the
/// CA certificate in the handshake: the selector path cannot send a chain whose root is not in a system store.
/// <para>
/// The client certificate is optional (enrollment has none). When one is sent it must chain to an instance CA with the
/// client authentication usage, validated with a custom root trust and no revocation checks; the allow list decides
/// afterwards whether it may open a session.
/// </para>
/// </summary>
public sealed class AgentTlsOptions
{
    private readonly GatewayCertificateProvider _certificates;
    private readonly CertificateAllowList _allowList;
    private volatile Cached? _cached;

    public AgentTlsOptions(GatewayCertificateProvider certificates, CertificateAllowList allowList)
    {
        _certificates = certificates;
        _allowList = allowList;
    }

    public ValueTask<SslServerAuthenticationOptions> OnConnectionAsync(TlsHandshakeCallbackContext context) =>
        ValueTask.FromResult(GetOptions());

    /// <summary>Options for the next handshake. Throws (which aborts the handshake) while no certificate or CA is loaded.</summary>
    internal SslServerAuthenticationOptions GetOptions()
    {
        var certificate = _certificates.Current
            ?? throw new AuthenticationException("The gateway has no server certificate yet; refusing the connection.");
        if (!_allowList.IsLoaded || _allowList.CaCertificates.Count == 0)
        {
            throw new AuthenticationException("The agent certificate allow list is not loaded yet; refusing the connection.");
        }

        var caSetKey = _allowList.CaSetKey;
        var cached = _cached;
        if (cached is not null && ReferenceEquals(cached.Certificate, certificate) && cached.CaSetKey == caSetKey)
        {
            return cached.Options;
        }

        var chainPolicy = CertificateChains.CreatePolicy(_allowList.CaCertificates, CertificateChains.ClientAuthOid);
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = certificate.Context,
            // Request, never require: enrollment comes without a client certificate.
            ClientCertificateRequired = true,
            CertificateChainPolicy = chainPolicy,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowRenegotiation = false,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            RemoteCertificateValidationCallback = ValidateClientCertificate
        };

        _cached = new Cached(certificate, caSetKey, options);
        return options;
    }

    /// <summary>
    /// No certificate: accepted (enrollment). A certificate: accepted when the chain built with the custom-root policy above has
    /// no errors, or when its only problem is that the agent certificate itself is outside its validity period (0.2.0): an
    /// expired agent must still be able to prove its key for recovery. Every path decides again after the handshake: a session
    /// needs a valid certificate on the allow list, recovery an expired one within the grace period.
    /// </summary>
    internal static bool ValidateClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return true;
        }

        return errors == SslPolicyErrors.None || (errors == SslPolicyErrors.RemoteCertificateChainErrors && OnlyLeafTimeInvalid(chain));
    }

    /// <summary>True when the chain reaches a trusted root and its only error is the time validity of the leaf certificate.</summary>
    internal static bool OnlyLeafTimeInvalid(X509Chain? chain)
    {
        if (chain is null || chain.ChainElements.Count < 2)
        {
            return false;
        }

        for (var i = 0; i < chain.ChainElements.Count; i++)
        {
            foreach (var status in chain.ChainElements[i].ChainElementStatus)
            {
                if (status.Status == X509ChainStatusFlags.NoError || (i == 0 && status.Status == X509ChainStatusFlags.NotTimeValid))
                {
                    continue;
                }

                return false;
            }
        }

        return chain.ChainStatus.All(s => s.Status is X509ChainStatusFlags.NoError or X509ChainStatusFlags.NotTimeValid);
    }

    private sealed record Cached(ServerCertificate Certificate, string CaSetKey, SslServerAuthenticationOptions Options);
}
