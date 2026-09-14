using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleetify.Infrastructure.Security;

namespace Fleetify.Infrastructure.Licensing;

/// <summary>Content of a Fleeto license, signed by Steaan with the license signing key.</summary>
public sealed record LicenseDocument
{
    public int FormatVersion { get; init; } = 1;
    public string Serial { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>The instance FQDN the license is valid for, compared case-insensitively.</summary>
    public string Fqdn { get; init; } = string.Empty;

    public int ManagedEndpointCount { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public sealed record LicenseVerification(bool IsValid, LicenseDocument? Document, string KeyId, string? Problem)
{
    public static LicenseVerification Fail(string problem) => new(false, null, string.Empty, problem);
}

/// <summary>
/// Text format of a license file:
/// <code>
/// -----BEGIN FLEETO LICENSE-----
/// &lt;base64 payload (UTF-8 JSON of LicenseDocument)&gt;
/// &lt;base64 ed25519 signature over "fleetify-license-v1" || 0x00 || payload&gt;
/// -----END FLEETO LICENSE-----
/// </code>
/// The instance verifies offline against the Steaan license public keys compiled into the build.
/// </summary>
public static class LicenseCodec
{
    private const string Begin = "-----BEGIN FLEETO LICENSE-----";
    private const string End = "-----END FLEETO LICENSE-----";
    private const int MaxLength = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Sign(LicenseDocument document, byte[] licensePrivateKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var signature = Ed25519.Sign(licensePrivateKey, SignatureContexts.License, payload);
        return $"{Begin}\n{Convert.ToBase64String(payload)}\n{Convert.ToBase64String(signature)}\n{End}\n";
    }

    public static LicenseVerification Verify(string? text, IReadOnlyList<byte[]> trustedPublicKeys, string expectedFqdn)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return LicenseVerification.Fail("The file is not a Fleeto license.");
        }

        var lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 4 || lines[0] != Begin || lines[3] != End)
        {
            return LicenseVerification.Fail("The file is not a Fleeto license.");
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(lines[1]);
            signature = Convert.FromBase64String(lines[2]);
        }
        catch (FormatException)
        {
            return LicenseVerification.Fail("The license file is damaged. Download it again.");
        }

        if (trustedPublicKeys.Count == 0)
        {
            return LicenseVerification.Fail(
                "This build has no license verification key. Use an official Fleeto build or configure the development keys.");
        }

        var key = trustedPublicKeys.FirstOrDefault(k => Ed25519.Verify(k, SignatureContexts.License, payload, signature));
        if (key is null)
        {
            return LicenseVerification.Fail("The license signature is not valid. Contact Steaan for a new license.");
        }

        LicenseDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<LicenseDocument>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return LicenseVerification.Fail("The license content is damaged. Contact Steaan for a new license.");
        }

        if (document is null || document.FormatVersion != 1 || document.ManagedEndpointCount < 0 || string.IsNullOrWhiteSpace(document.Serial))
        {
            return LicenseVerification.Fail("The license format is not supported by this version of Fleeto.");
        }

        if (!string.Equals(document.Fqdn.TrimEnd('.'), expectedFqdn.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            return LicenseVerification.Fail(
                $"This license is for {document.Fqdn}, but this instance runs on {expectedFqdn}. Load the license issued for this instance.");
        }

        return new LicenseVerification(true, document, KeyIds.For(key), null);
    }
}

/// <summary>
/// Public keys compiled into the build through MSBuild properties (Directory.Build.props). Values are
/// semicolon-separated base64 ed25519 public keys: the current key first, then standby keys.
/// </summary>
public static class TrustedKeys
{
    public static IReadOnlyList<byte[]> LicenseKeys { get; } = Read("Fleetify.LicensePublicKeys");
    public static IReadOnlyList<byte[]> ReleaseKeys { get; } = Read("Fleetify.ReleasePublicKeys");

    private static IReadOnlyList<byte[]> Read(string name)
    {
        var value = typeof(Core.Domain.LicenseStatus).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == name)?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var keys = new List<byte[]>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var key = Convert.FromBase64String(part);
                if (key.Length == Ed25519.PublicKeySize)
                {
                    keys.Add(key);
                }
            }
            catch (FormatException)
            {
                // A malformed key is ignored; verification then fails closed.
            }
        }

        return keys;
    }

    internal static string Describe(IReadOnlyList<byte[]> keys) =>
        string.Join(", ", keys.Select(k => KeyIds.For(k))) is { Length: > 0 } ids ? ids : "none";

    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
