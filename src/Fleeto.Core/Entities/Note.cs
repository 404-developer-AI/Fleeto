namespace Fleeto.Core.Entities;

/// <summary>
/// A markdown note on an endpoint, written by a technician. Personal data may appear in the body (GDPR): it is deleted with
/// the endpoint and never copied into the audit log.
/// </summary>
public class Note
{
    public const int MaxBodyLength = 20000;

    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }

    /// <summary>No foreign key to the user: the note stays readable after the account is deleted.</summary>
    public Guid AuthorUserId { get; set; }

    /// <summary>Display name of the author when the note was written.</summary>
    public string AuthorName { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Set when the author changed the body after creating the note.</summary>
    public DateTime? EditedAt { get; set; }
}
