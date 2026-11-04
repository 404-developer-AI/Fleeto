namespace Fleeto.Core.Domain;

/// <summary>
/// The texts of the consent prompt of remote control on a workstation (0.8.0, decided 2026-11-04), set per policy so a client can have its
/// own words and language. A text left empty is the agent's own English default. The prompt always names the technician and shows the
/// seconds left itself, outside these texts, so a policy can never hide who is asking.
/// </summary>
public sealed record ConsentTexts(string? Title, string? Message, string? AllowLabel, string? DenyLabel)
{
    public static ConsentTexts Default { get; } = new(null, null, null, null);

    public bool IsDefault => Title is null && Message is null && AllowLabel is null && DenyLabel is null;
}

/// <summary>Limits and cleaning of <see cref="ConsentTexts"/>; the web, the signer and the agent hold the same limits.</summary>
public static class ConsentTextRules
{
    public const int MaxTitleLength = 60;
    public const int MaxMessageLength = 400;
    public const int MaxMessageLines = 8;
    public const int MaxLabelLength = 20;

    /// <summary>The defaults the agent shows for an empty text, also shown as placeholders in the policy editor.</summary>
    public const string DefaultTitle = "Remote control request";

    public const string DefaultMessage = "wants to view and control this computer through Fleeto remote control. Allow the session?";
    public const string DefaultAllowLabel = "Allow";
    public const string DefaultDenyLabel = "Deny";

    /// <summary>
    /// The texts to store (trimmed, control characters removed, an empty text null), or the problem to show. The message keeps its line
    /// breaks; the title and the labels are one line.
    /// </summary>
    public static (ConsentTexts? Texts, string? Problem) Clean(ConsentTexts? texts)
    {
        if (texts is null)
        {
            return (ConsentTexts.Default, null);
        }

        var title = Line(texts.Title);
        var allow = Line(texts.AllowLabel);
        var deny = Line(texts.DenyLabel);
        var message = Lines(texts.Message);
        if (title is { Length: > MaxTitleLength })
        {
            return (null, $"The title of the consent prompt has at most {MaxTitleLength} characters.");
        }

        if (message is { Length: > MaxMessageLength })
        {
            return (null, $"The message of the consent prompt has at most {MaxMessageLength} characters.");
        }

        if (message is not null && message.Split('\n').Length > MaxMessageLines)
        {
            return (null, $"The message of the consent prompt has at most {MaxMessageLines} lines.");
        }

        if (allow is { Length: > MaxLabelLength } || deny is { Length: > MaxLabelLength })
        {
            return (null, $"A button of the consent prompt has at most {MaxLabelLength} characters.");
        }

        if (allow is not null && deny is not null && string.Equals(allow, deny, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The two buttons of the consent prompt need different labels, so the person at the endpoint can tell them apart.");
        }

        return (new ConsentTexts(title, message, allow, deny), null);
    }

    private static string? Line(string? value)
    {
        var text = new string((value ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Length == 0 ? null : text;
    }

    private static string? Lines(string? value)
    {
        var lines = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(l => Line(l) ?? string.Empty)
            .ToList();
        // No empty lines at the start or the end, and at most one empty line between paragraphs.
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
            {
                continue;
            }

            kept.Add(line);
        }

        while (kept.Count > 0 && kept[^1].Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept.Count == 0 ? null : string.Join('\n', kept);
    }
}
