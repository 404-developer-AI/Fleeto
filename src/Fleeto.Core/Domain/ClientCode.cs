using System.Text.RegularExpressions;

namespace Fleeto.Core.Domain;

/// <summary>Rules for client codes: short, unique, uppercase.</summary>
public static partial class ClientCode
{
    public const int MaxLength = 16;

    /// <summary>Uppercases and trims user input. Validation happens in <see cref="Validate"/>.</summary>
    public static string Normalize(string? input) => (input ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Returns a problem message, or null when the (normalized) code is valid.</summary>
    public static string? Validate(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "Enter a client code.";
        }

        if (code.Length > MaxLength)
        {
            return $"The client code can be at most {MaxLength} characters.";
        }

        return CodePattern().IsMatch(code)
            ? null
            : "Use only letters A to Z, digits and hyphens in the client code, starting with a letter or digit.";
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]*$")]
    private static partial Regex CodePattern();
}
