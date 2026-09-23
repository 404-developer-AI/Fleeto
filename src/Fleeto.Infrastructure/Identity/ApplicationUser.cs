using Microsoft.AspNetCore.Identity;

namespace Fleeto.Infrastructure.Identity;

/// <summary>A Fleeto user. Two-factor authentication is mandatory; see the web login flow.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// The <c>oid</c> claim of the Entra ID account this user signs in with (0.5.0), or null for a local account. An admin
    /// links it; Fleeto never creates a user from a token. Matched together with <see cref="EntraTenantId"/>, never on the
    /// email address, which changes and can be given to somebody else.
    /// </summary>
    public Guid? EntraObjectId { get; set; }

    /// <summary>The <c>tid</c> claim of the account, so a link cannot be used from another tenant.</summary>
    public string? EntraTenantId { get; set; }

    /// <summary>The account name of the linked Entra ID account, for display in Settings.</summary>
    public string? EntraAccount { get; set; }

    public DateTime? EntraLinkedAt { get; set; }

    /// <summary>This user signs in with Entra ID.</summary>
    public bool IsLinkedToEntra => EntraObjectId is not null;
}

public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string name) : base(name)
    {
    }
}
