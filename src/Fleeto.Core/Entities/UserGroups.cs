namespace Fleeto.Core.Entities;

/// <summary>
/// A group of users and what it gives access to (0.6.0): clients, sites and endpoints, or every client with exceptions.
/// A user that is not an admin sees only what its groups give it together; without a group it sees nothing. The role stays on
/// the user. Admins are outside groups and see everything.
/// </summary>
public class UserGroup
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Upper case of <see cref="Name"/>, unique, so two groups cannot differ only in case.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Every client, the ones created later included, except what the exclude rules name. Without it the group gives what
    /// its include rules name, minus its exclude rules.
    /// </summary>
    public bool AllClients { get; set; }

    /// <summary>
    /// The group "All clients" the update to 0.6.0 put every existing user in, so nobody lost access. A user added to any
    /// other group leaves it.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<UserGroupMember> Members { get; set; } = [];
    public List<UserGroupRule> Rules { get; set; } = [];
}

/// <summary>A user in a group.</summary>
public class UserGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public DateTime AddedAt { get; set; }
}

public enum UserGroupRuleKind
{
    /// <summary>The group gives access to the target, with everything under it.</summary>
    Include,

    /// <summary>The group does not give access to the target or anything under it. Counts only inside its own group.</summary>
    Exclude
}

/// <summary>
/// A client, a site or an endpoint a group includes or excludes. Exactly one of the three ids is set; the rule is removed with
/// its target.
/// </summary>
public class UserGroupRule
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public UserGroupRuleKind Kind { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? EndpointId { get; set; }
}
