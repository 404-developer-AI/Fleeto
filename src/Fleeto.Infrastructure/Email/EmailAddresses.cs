namespace Fleeto.Infrastructure.Email;

/// <summary>Recipient addresses of instance emails: splitting a list and a cheap sanity check.</summary>
public static class EmailAddresses
{
    /// <summary>Splits a comma- or semicolon-separated recipient list into plausible, distinct addresses.</summary>
    public static IReadOnlyList<string> Split(string? recipients) =>
        (recipients ?? string.Empty)
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(IsPlausible)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>A cheap sanity check; MimeKit parses the address properly at delivery time.</summary>
    public static bool IsPlausible(string address) =>
        address.Length is > 2 and <= 320 && address.IndexOf('@') > 0 && address.IndexOf('@') < address.Length - 1 &&
        !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
