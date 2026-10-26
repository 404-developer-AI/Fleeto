namespace Fleeto.Core.Entities;

// Security advisories on the dashboard (0.8.0 step 0, VULNERABILITIES.md §2): instance-wide, no client. A warning is information
// for the team and is never matched to the inventory.

/// <summary>A source of advisories (a key of <see cref="Domain.AdvisorySources"/>) and the state of its last fetch.</summary>
public class AdvisorySource
{
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>The first failed attempt since the last success; null while the source works.</summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>Why the last attempt failed, with the next step; null when it worked.</summary>
    public string? Problem { get; set; }
}

/// <summary>One warning of a source. Every text in it comes from the source and is shown as plain text.</summary>
public class Advisory
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>The id the source gives the warning (the guid of an RSS item); unique per source.</summary>
    public string SourceId { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Null when the source gives no summary (the CCB does not).</summary>
    public string? Summary { get; set; }

    public DateTime PublishedAt { get; set; }

    /// <summary>The CVE identifiers the source names; empty when it names none or gives none (the CCB).</summary>
    public string[] Cves { get; set; } = [];

    /// <summary>The link to the warning, checked against the host of its source; null when the source gave none that passed.</summary>
    public string? Link { get; set; }

    /// <summary>When Fleeto first stored it; a later change by its source does not move it, so it is not new again.</summary>
    public DateTime FetchedAt { get; set; }
}

/// <summary>A warning marked as handled by a user, with a required comment. One per warning.</summary>
public class AdvisoryHandling
{
    public Guid AdvisoryId { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public DateTime HandledAt { get; set; }
}
