using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>The script version a script check runs on one endpoint, or why it cannot run.</summary>
public sealed record ScriptCheckScript(Script? Script, ScriptVersion? Version, string? UnavailableReason);

/// <summary>
/// Decides which library script version a script check runs on an endpoint (0.2.0). Used by the configuration builder in the signer,
/// so the rules hold whatever web stored: the script exists, is global or of the endpoint's client, its language runs on the
/// endpoint, and its body still has its hash. Without approval required the current version runs. When the endpoint's policy requires
/// approval the newest version approved by an admin other than its author runs, so a check keeps working with the approved body
/// while a change waits for approval (a job, started by hand, only runs the current version).
/// </summary>
public static class ScriptCheckResolver
{
    public const string DeletedReason = "The script of this check was deleted. Choose another script for the check.";
    public const string ClientReason = "The script of this check belongs to another client. Choose a global script or one of this client.";
    public const string PlatformReason = "The script's language does not run on this endpoint's operating system.";
    public const string NotApprovedReason =
        "The site's policy requires approval of scripts, and no version of this script is approved by a second admin. Ask an admin to approve it.";
    public const string TooLargeReason =
        "The scripts of this endpoint's script checks together exceed 1 MiB, the most one configuration carries. Use smaller scripts or fewer script checks.";

    public static readonly string AdminRole = FleetoRoles.Admin.ToUpperInvariant();
    public static readonly string TechnicianRole = FleetoRoles.Technician.ToUpperInvariant();

    public static async Task<ScriptCheckScript> ResolveAsync(FleetoDbContext db, string? scriptIdText, Guid endpointClientId, string? osPlatform,
        bool approvalRequired, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(scriptIdText, "D", out var scriptId))
        {
            return new ScriptCheckScript(null, null, DeletedReason);
        }

        var script = await db.Scripts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken);
        if (script is null)
        {
            return new ScriptCheckScript(null, null, DeletedReason);
        }

        if (script.ClientId is { } scriptClient && scriptClient != endpointClientId)
        {
            return new ScriptCheckScript(script, null, ClientReason);
        }

        if (!ScriptLanguages.RunsOn(script.Language, osPlatform))
        {
            return new ScriptCheckScript(script, null, PlatformReason);
        }

        if (!approvalRequired)
        {
            var current = await db.ScriptVersions.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == script.CurrentVersionId && v.ScriptId == script.Id, cancellationToken);
            return current is not null && Intact(script, current)
                ? new ScriptCheckScript(script, current, null)
                : new ScriptCheckScript(script, null, DeletedReason);
        }

        var approved = await db.ScriptVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.ScriptId == script.Id && v.ApprovedByUserId != null && v.ApprovedByUserId != v.AuthorUserId && v.ApprovedSha256 == v.Sha256)
            .OrderByDescending(v => v.Number)
            .Take(20)
            .ToListAsync(cancellationToken);
        foreach (var version in approved)
        {
            if (version.IsApproved && Intact(script, version) &&
                await UserHasRoleAsync(db, version.ApprovedByUserId!.Value, null, cancellationToken, AdminRole))
            {
                return new ScriptCheckScript(script, version, null);
            }
        }

        return new ScriptCheckScript(script, null, NotApprovedReason);
    }

    /// <summary>
    /// True when the user exists, has two-factor authentication, holds one of the normalized roles and, with <paramref name="lockedOutAt"/>,
    /// is not locked out at that time. A user linked to Entra ID counts as having two factors: its second factor comes from
    /// Microsoft, and web lets such a session in only with one (0.5.0). The signer cannot see the session, only the link.
    /// </summary>
    public static async Task<bool> UserHasRoleAsync(FleetoDbContext db, Guid userId, DateTime? lockedOutAt, CancellationToken cancellationToken,
        params string[] normalizedRoles)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.LockoutEnd, u.TwoFactorEnabled, Linked = u.EntraObjectId != null })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || !(user.TwoFactorEnabled || user.Linked) ||
            (lockedOutAt is { } now && user.LockoutEnd > new DateTimeOffset(now, TimeSpan.Zero)))
        {
            return false;
        }

        return await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.NormalizedName)
            .AnyAsync(name => normalizedRoles.Contains(name), cancellationToken);
    }

    private static bool Intact(Script script, ScriptVersion version) =>
        version.ClientId == script.ClientId && ScriptLanguages.Sha256(version.Body) == version.Sha256;
}
