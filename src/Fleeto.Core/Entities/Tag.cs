namespace Fleeto.Core.Entities;

/// <summary>
/// A tag of the instance (0.6.0), created the first time somebody types it on a client. Instance-wide on purpose, so one name
/// has one color everywhere; which clients carry it is in <see cref="ClientTag"/>, which is client-owned.
/// </summary>
public class Tag
{
    public Guid Id { get; set; }

    /// <summary>As first typed; <see cref="NormalizedName"/> is the unique key.</summary>
    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public TagColor Color { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A tag on a client.</summary>
public class ClientTag
{
    public Guid ClientId { get; set; }
    public Guid TagId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Tag? Tag { get; set; }
}

/// <summary>The fixed palette of tag colors: each works as a tinted chip with dark text on the light theme.</summary>
public enum TagColor
{
    Red,
    Orange,
    Amber,
    Lime,
    Green,
    Teal,
    Cyan,
    Blue,
    Pink,
    Brown,
    Gray
}
