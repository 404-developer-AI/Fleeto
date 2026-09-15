using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Fleeto.Infrastructure.Hosting;

/// <summary>Where secret files (root key, signer key, database passwords) are read from.</summary>
public sealed class SecretsOptions
{
    public const string SectionName = "Fleeto:Secrets";

    /// <summary>
    /// Directory with the secret files. Empty means: the <c>FLEETO_SECRETS_DIR</c> environment variable, else
    /// <c>%LOCALAPPDATA%\Fleeto\dev\secrets</c> on Windows (local development), else <c>/run/secrets</c>
    /// (Docker secrets on the VPS).
    /// </summary>
    public string Directory { get; set; } = string.Empty;
}

/// <summary>
/// Reads secret files. Secrets are never taken from configuration values or environment variables, only from
/// root-owned files outside the repository (CLAUDE.md, Secrets). Values are never logged.
/// </summary>
public sealed class SecretFiles
{
    public const string RootKeyFile = "root.key";
    public const string SignerKeyFile = "signer.key";

    public SecretFiles(IOptions<SecretsOptions> options)
    {
        Directory = ResolveDirectory(options.Value.Directory);
    }

    public SecretFiles(string directory)
    {
        Directory = ResolveDirectory(directory);
    }

    public string Directory { get; }

    public static SecretFiles FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetSection(SecretsOptions.SectionName).GetValue<string>("Directory") ?? string.Empty);

    /// <summary>Reads a secret file and trims surrounding whitespace.</summary>
    public string ReadText(string fileName)
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Secret file '{fileName}' was not found in '{Directory}'. On a VPS, run install.sh for this instance; " +
                "for local development, run tools/dev/setup-dev.ps1.");
        }

        return File.ReadAllText(path).Trim();
    }

    /// <summary>Reads a 32-byte key stored as base64.</summary>
    public byte[] ReadKey(string fileName)
    {
        var text = ReadText(fileName);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Secret file '{fileName}' does not contain a base64 key.");
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException($"Secret file '{fileName}' must contain exactly 32 bytes, found {key.Length}.");
        }

        return key;
    }

    public bool Exists(string fileName) => File.Exists(Path.Combine(Directory, fileName));

    private static string ResolveDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("FLEETO_SECRETS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fleeto", "dev", "secrets")
            : "/run/secrets";
    }
}
