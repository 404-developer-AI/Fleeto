using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Fleetify.Infrastructure.Security;

/// <summary>
/// ed25519 signatures (BouncyCastle; not in the .NET base class library). Every signature in Fleeto uses a
/// domain separation context: the signed message is <c>context || 0x00 || payload</c>, so a signature made for
/// one purpose (a license) can never be valid for another (an agent configuration).
/// </summary>
public static class Ed25519
{
    public const int PrivateKeySize = 32;
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    /// <summary>Generates a key pair. The private key is the 32-byte seed.</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
    }

    public static byte[] PublicKeyFromPrivate(ReadOnlySpan<byte> privateKey) =>
        new Ed25519PrivateKeyParameters(privateKey.ToArray()).GeneratePublicKey().GetEncoded();

    public static byte[] Sign(ReadOnlySpan<byte> privateKey, string context, ReadOnlySpan<byte> payload)
    {
        if (privateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException("An ed25519 private key is 32 bytes.", nameof(privateKey));
        }

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey.ToArray()));
        var message = BuildMessage(context, payload);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, string context, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
            var message = BuildMessage(context, payload);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Plain ed25519 over raw bytes, without a context prefix. Only for release artifacts (install.sh, the release
    /// manifest, agent binaries), because customers verify those with <c>openssl pkeyutl -verify -rawin</c>, which
    /// cannot add a prefix. The release key signs nothing else, which gives the same separation.
    /// </summary>
    public static byte[] SignRaw(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey.ToArray()));
        var bytes = data.ToArray();
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.GenerateSignature();
    }

    public static bool VerifyRaw(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
        var bytes = data.ToArray();
        verifier.BlockUpdate(bytes, 0, bytes.Length);
        return verifier.VerifySignature(signature.ToArray());
    }

    /// <summary>PEM SubjectPublicKeyInfo, the format <c>openssl pkeyutl -pubin</c> expects.</summary>
    public static string PublicKeyToPem(ReadOnlySpan<byte> publicKey)
    {
        // SEQUENCE { SEQUENCE { OID 1.3.101.112 } BIT STRING key }
        byte[] prefix = [0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];
        var der = new byte[prefix.Length + publicKey.Length];
        prefix.CopyTo(der, 0);
        publicKey.CopyTo(der.AsSpan(prefix.Length));
        return $"-----BEGIN PUBLIC KEY-----\n{Convert.ToBase64String(der)}\n-----END PUBLIC KEY-----\n";
    }

    private static byte[] BuildMessage(string context, ReadOnlySpan<byte> payload)
    {
        var contextBytes = Encoding.ASCII.GetBytes(context);
        var message = new byte[contextBytes.Length + 1 + payload.Length];
        contextBytes.CopyTo(message, 0);
        message[contextBytes.Length] = 0;
        payload.CopyTo(message.AsSpan(contextBytes.Length + 1));
        return message;
    }
}

/// <summary>Signature contexts. Adding a purpose means adding a constant here, never reusing one.</summary>
public static class SignatureContexts
{
    /// <summary>Must equal ProtocolLimits.ConfigSignatureContext and the Go agent constant.</summary>
    public const string AgentConfig = "fleetify-agent-config-v1";
    public const string License = "fleetify-license-v1";
    public const string ReleaseManifest = "fleetify-release-manifest-v1";
}

/// <summary>Constant-time comparison helpers.</summary>
public static class SecureCompare
{
    public static bool HexEquals(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
