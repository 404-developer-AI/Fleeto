using System.Security.Cryptography;
using System.Text;
using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

/// <summary>Script languages per platform, normalization and hashing (0.2.0).</summary>
public static class ScriptLanguages
{
    /// <summary>The endpoint platforms (OsPlatform) a language runs on.</summary>
    public static IReadOnlyList<string> Platforms(ScriptLanguage language) => language switch
    {
        ScriptLanguage.PowerShell or ScriptLanguage.Batch => ["windows"],
        _ => ["linux", "darwin"]
    };

    public static bool RunsOn(ScriptLanguage language, string? osPlatform) =>
        osPlatform is not null && Platforms(language).Contains(osPlatform, StringComparer.OrdinalIgnoreCase);

    public static string Label(ScriptLanguage language) => language switch
    {
        ScriptLanguage.PowerShell => "PowerShell",
        ScriptLanguage.Batch => "Batch",
        ScriptLanguage.Shell => "sh",
        _ => "bash"
    };

    public static string PlatformLabel(ScriptLanguage language) =>
        language is ScriptLanguage.PowerShell or ScriptLanguage.Batch ? "Windows" : "Linux";

    /// <summary>
    /// The body as stored and signed: Batch uses CRLF line endings (cmd.exe misreads labels otherwise), every other language LF.
    /// A trailing line break is kept as entered.
    /// </summary>
    public static string Normalize(ScriptLanguage language, string body)
    {
        var lf = body.Replace("\r\n", "\n").Replace('\r', '\n');
        return language == ScriptLanguage.Batch ? lf.Replace("\n", "\r\n") : lf;
    }

    public static string Sha256(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>Returns a problem (cause and next step), or null when the body can be saved.</summary>
    public static string? Validate(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "Enter the script.";
        }

        if (body.Length > ScriptRules.MaxBodyLength || Encoding.UTF8.GetByteCount(body) > ScriptRules.MaxBodyLength)
        {
            return $"A script can be at most {ScriptRules.MaxBodyLength / 1024} KiB. Split it into smaller scripts.";
        }

        return body.Contains('\0') ? "The script contains a null character. Remove it and try again." : null;
    }
}
