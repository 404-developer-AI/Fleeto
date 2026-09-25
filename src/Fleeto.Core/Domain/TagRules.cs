using System.Text;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// Rules for tags on clients (0.6.0), in the way of Proxmox: typed freely on a client, one name across the instance, compared
/// without regard to case, and colored from a fixed palette. A new tag takes the color its name points at, so the same name gets
/// the same color on every instance until an admin picks another one.
/// </summary>
public static class TagRules
{
    public const int MaxLength = 32;
    public const int MaxPerClient = 10;

    /// <summary>The colors in the order of the palette in Settings. Gray is last: it is the color of nothing in particular.</summary>
    public static IReadOnlyList<TagColor> Palette { get; } = Enum.GetValues<TagColor>();

    /// <summary>Trims user input. Validation happens in <see cref="Validate"/>.</summary>
    public static string Clean(string? input) => (input ?? string.Empty).Trim();

    /// <summary>The key a name is unique by: two tags that differ only in case are one tag.</summary>
    public static string Normalize(string name) => Clean(name).ToUpperInvariant();

    /// <summary>Returns a problem message, or null when the (cleaned) name is valid.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Enter a tag name.";
        }

        if (name.Length > MaxLength)
        {
            return $"A tag can be at most {MaxLength} characters.";
        }

        return name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '+')
            ? null
            : $"Tag {name} contains characters that are not allowed. Use letters, digits and - _ . + without spaces.";
    }

    /// <summary>
    /// The color a new tag gets: a stable hash (FNV-1a) of its normalized name over the palette without gray. Never
    /// <see cref="string.GetHashCode()"/>, which differs per process.
    /// </summary>
    public static TagColor DefaultColor(string name)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(Normalize(name)))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return Palette[(int)(hash % (uint)(Palette.Count - 1))];
    }
}
