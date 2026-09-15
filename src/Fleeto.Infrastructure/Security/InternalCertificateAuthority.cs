using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// The internal CA of an instance. ECDSA P-256 throughout: Windows SChannel (Kestrel on Windows, the Windows
/// agent's platform key store) does not support ed25519 certificates in TLS. Application signatures stay ed25519.
/// </summary>
public static class InternalCertificateAuthority
{
    public static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3650);
    public static readonly TimeSpan AgentCertificateLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan GatewayCertificateLifetime = TimeSpan.FromHours(24);

    /// <summary>Tolerance for clock differences between signer and agent when setting NotBefore.</summary>
    private static readonly TimeSpan BackdateBy = TimeSpan.FromMinutes(5);

    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    public sealed record CaMaterial(byte[] CertificateDer, byte[] PrivateKeyPkcs8, string Fingerprint, DateTime ExpiresAt);

    public sealed record IssuedCertificate(byte[] CertificateDer, string Fingerprint, string PublicKeyFingerprint,
        string SerialNumber, DateTime NotBefore, DateTime NotAfter);

    public static CaMaterial CreateCa(string instanceFqdn, DateTime now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=Fleeto Instance CA {Sanitize(instanceFqdn)}, O=Fleeto", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notAfter = now + CaLifetime;
        using var certificate = request.CreateSelfSigned(now - BackdateBy, notAfter);
        var der = certificate.Export(X509ContentType.Cert);
        return new CaMaterial(der, key.ExportPkcs8PrivateKey(), KeyIds.Sha256Hex(der), notAfter);
    }

    /// <summary>
    /// Issues an agent client certificate from a CSR. The CSR signature proves possession of the key. Subject and
    /// extensions come from the signer, never from the CSR.
    /// </summary>
    public static IssuedCertificate IssueAgentCertificate(byte[] caCertificateDer, byte[] caPrivateKeyPkcs8, byte[] csrDer,
        Guid endpointId, Guid instanceId, DateTime now)
    {
        var csr = LoadCsr(csrDer);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={endpointId:D}, O=Fleeto"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri($"urn:fleeto:endpoint:{endpointId:D}"));
        san.AddUri(new Uri($"urn:fleeto:instance:{instanceId:D}"));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return Issue(caCertificateDer, caPrivateKeyPkcs8, request, now, AgentCertificateLifetime);
    }

    /// <summary>Issues the short-lived gateway server certificate for the agent host names.</summary>
    public static IssuedCertificate IssueGatewayCertificate(byte[] caCertificateDer, byte[] caPrivateKeyPkcs8, byte[] csrDer,
        IEnumerable<string> dnsNames, DateTime now)
    {
        var csr = LoadCsr(csrDer);
        var names = dnsNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException("A gateway certificate needs at least one host name.");
        }

        var request = new CertificateRequest(new X500DistinguishedName($"CN={Sanitize(names[0])}, O=Fleeto"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (System.Net.IPAddress.TryParse(name, out var address))
            {
                san.AddIpAddress(address);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        return Issue(caCertificateDer, caPrivateKeyPkcs8, request, now, GatewayCertificateLifetime);
    }

    /// <summary>SHA-256 hex of a certificate's SubjectPublicKeyInfo.</summary>
    public static string PublicKeyFingerprint(X509Certificate2 certificate) =>
        KeyIds.Sha256Hex(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>SHA-256 hex of the public key in a CSR (after verifying the CSR signature).</summary>
    public static string CsrPublicKeyFingerprint(byte[] csrDer) =>
        KeyIds.Sha256Hex(LoadCsr(csrDer).PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>Extracts the endpoint id from an agent certificate's SAN URI.</summary>
    public static Guid? EndpointIdFromCertificate(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension)
            {
                continue;
            }

            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                // uniformResourceIdentifier [6] IA5String
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    var uri = sequence.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
                    // Certificates issued before the rename carry the legacy URI until they are renewed.
                    foreach (var prefix in (ReadOnlySpan<string>)["urn:fleeto:endpoint:", LegacyNames.EndpointUriPrefix])
                    {
                        if (uri.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(uri[prefix.Length..], out var id))
                        {
                            return id;
                        }
                    }
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }

        return null;
    }

    private static CertificateRequest LoadCsr(byte[] csrDer)
    {
        CertificateRequest csr;
        try
        {
            // Verifies the CSR self-signature: proof of possession of the private key.
            csr = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The certificate signing request is invalid or its signature does not verify.", ex);
        }

        using var key = csr.PublicKey.GetECDsaPublicKey()
            ?? throw new InvalidOperationException("The certificate signing request must use an ECDSA P-256 key.");
        var parameters = key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value &&
            parameters.Curve.Oid.FriendlyName != ECCurve.NamedCurves.nistP256.Oid.FriendlyName)
        {
            throw new InvalidOperationException("The certificate signing request must use an ECDSA P-256 key.");
        }

        return csr;
    }

    private static IssuedCertificate Issue(byte[] caCertificateDer, byte[] caPrivateKeyPkcs8, CertificateRequest request,
        DateTime now, TimeSpan lifetime)
    {
        using var caKey = ECDsa.Create();
        caKey.ImportPkcs8PrivateKey(caPrivateKeyPkcs8, out _);
        using var caPublic = X509CertificateLoader.LoadCertificate(caCertificateDer);
        using var caCertificate = caPublic.CopyWithPrivateKey(caKey);

        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCertificate, true, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        var notBefore = now - BackdateBy;
        var notAfter = now + lifetime;
        if (notAfter > caCertificate.NotAfter.ToUniversalTime())
        {
            notAfter = caCertificate.NotAfter.ToUniversalTime();
        }

        using var issued = request.Create(caCertificate, notBefore, notAfter, serial);
        var der = issued.Export(X509ContentType.Cert);
        return new IssuedCertificate(der, KeyIds.Sha256Hex(der), PublicKeyFingerprint(issued), Convert.ToHexStringLower(serial),
            notBefore, notAfter);
    }

    private static string Sanitize(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());
}
