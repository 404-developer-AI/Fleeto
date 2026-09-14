using Microsoft.AspNetCore.Identity;

namespace Fleetify.Infrastructure.Identity;

/// <summary>A Fleeto user. Two-factor authentication is mandatory; see the web login flow.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
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
