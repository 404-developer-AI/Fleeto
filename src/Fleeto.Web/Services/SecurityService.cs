using System.Net;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Services;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record IpBlockView(Guid Id, string Network, IpBlockSource Source, string Reason, DateTime CreatedAt, DateTime? ExpiresAt,
    string CreatedByName, bool Active);

public sealed record IpAllowView(Guid Id, string Network, string Description, DateTime CreatedAt, string CreatedByName);

public sealed record SecurityIncidentView(Guid Id, SecurityIncidentKind Kind, AlertSeverity Severity, string Title, string Detail, string? Network,
    int Count, DateTime OpenedAt, DateTime LastSeenAt, DateTime? BlockedUntil, DateTime? AcknowledgedAt, string? AcknowledgedByName,
    string? AcknowledgeComment, DateTime? ResolvedAt, string? ResolvedByName, string? ResolveComment);

/// <summary>A blocklist as Settings, Security shows it (step 3). <paramref name="Configured"/> is false only for CrowdSec without an integration.</summary>
public sealed record IpFeedView(string Key, string Name, string Description, string TermsUrl, bool Enabled, bool Configured, int EntryCount,
    DateTime? LastAttemptAt, DateTime? LastSuccessAt, string? Problem, bool RefreshRequested, string? CrowdSecIntegrationId);

/// <summary>The security tile of the dashboard: open incidents that nobody acknowledged, the worst of them, and the active blocks.</summary>
public sealed record SecurityTile(int OpenIncidents, int Critical, string? Latest, int BlockedAddresses);

/// <summary>
/// The blocked and allowed addresses of the instance (0.7.0, security monitor), in Settings, Security. Instance-wide, so only
/// for a caller who sees every client. A change takes effect within seconds: a database trigger wakes web and the gateway,
/// which load the list again. Every change is in the audit log.
/// </summary>
public sealed class SecurityService
{
    public const int MaxReasonLength = 500;

    /// <summary>The longest a block with an end can last; longer is "until an admin lifts it".</summary>
    public static readonly TimeSpan MaxBlockDuration = TimeSpan.FromDays(365);

    /// <summary>Resolved incidents shown on the page.</summary>
    public static readonly TimeSpan ResolvedShown = TimeSpan.FromDays(7);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly SettingsStore _settings;

    public SecurityService(IFleetoDbContextFactory dbFactory, TimeProvider time, SettingsStore settings)
    {
        _dbFactory = dbFactory;
        _time = time;
        _settings = settings;
    }

    /// <summary>The open incidents, the most recent first, then those resolved in the last 7 days.</summary>
    public async Task<IReadOnlyList<SecurityIncidentView>> ListIncidentsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.SecurityView);
        var since = _time.GetUtcNow().UtcDateTime - ResolvedShown;
        await using var db = _dbFactory.CreateSystem();
        var incidents = await db.SecurityIncidents.AsNoTracking()
            .Where(i => i.ResolvedAt == null || i.ResolvedAt >= since)
            .OrderBy(i => i.ResolvedAt != null).ThenByDescending(i => i.LastSeenAt)
            .Take(500)
            .ToListAsync(cancellationToken);
        return incidents.Select(i => new SecurityIncidentView(i.Id, i.Kind, i.Severity, i.Title, i.Detail, i.Network, i.Count, i.OpenedAt, i.LastSeenAt,
            i.BlockedUntil, i.AcknowledgedAt, i.AcknowledgedByName, i.AcknowledgeComment, i.ResolvedAt, i.ResolvedByName, i.ResolveComment)).ToList();
    }

    /// <summary>The dashboard tile, or null for a caller without the permission.</summary>
    public async Task<SecurityTile?> GetTileAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityView))
        {
            return null;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var open = await db.SecurityIncidents.AsNoTracking().Where(i => i.ResolvedAt == null && i.AcknowledgedAt == null)
            .OrderByDescending(i => i.Severity).ThenByDescending(i => i.LastSeenAt)
            .Select(i => new { i.Severity, i.Title })
            .ToListAsync(cancellationToken);
        var blocked = await db.IpBlocks.AsNoTracking().CountAsync(b => b.ExpiresAt == null || b.ExpiresAt > now, cancellationToken);
        return new SecurityTile(open.Count, open.Count(i => i.Severity == AlertSeverity.Critical), open.FirstOrDefault()?.Title, blocked);
    }

    /// <summary>Acknowledges an open incident with a required comment: it stays open, but leaves the dashboard tile.</summary>
    public Task<ServiceResult> AcknowledgeAsync(Caller caller, Guid id, string? comment, CancellationToken cancellationToken = default) =>
        ChangeIncidentAsync(caller, id, comment, resolve: false, cancellationToken);

    /// <summary>
    /// Resolves an open incident by hand with a required comment. When the attack goes on, the next pass opens a new incident.
    /// A block of the incident stays until it ends or is lifted.
    /// </summary>
    public Task<ServiceResult> ResolveAsync(Caller caller, Guid id, string? comment, CancellationToken cancellationToken = default) =>
        ChangeIncidentAsync(caller, id, comment, resolve: true, cancellationToken);

    private async Task<ServiceResult> ChangeIncidentAsync(Caller caller, Guid id, string? comment, bool resolve, CancellationToken cancellationToken)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        var text = comment?.Trim() ?? string.Empty;
        if (text.Length is 0 or > SecurityIncident.MaxCommentLength)
        {
            return ServiceResult.Fail($"Enter a comment of at most {SecurityIncident.MaxCommentLength} characters: what you checked or did.");
        }

        await using var db = _dbFactory.CreateSystem();
        var incident = await db.SecurityIncidents.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (incident is null)
        {
            return ServiceResult.NotFound("incident");
        }

        if (incident.ResolvedAt is not null)
        {
            return ServiceResult.Fail("The incident is resolved already. Refresh the page.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (resolve)
        {
            incident.ResolvedAt = now;
            incident.ResolvedByName = caller.Name;
            incident.ResolveComment = text;
        }
        else
        {
            incident.AcknowledgedAt = now;
            incident.AcknowledgedByName = caller.Name;
            incident.AcknowledgeComment = text;
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(resolve ? AuditActions.SecurityIncidentResolved : AuditActions.SecurityIncidentAcknowledged,
            "SecurityIncident", incident.Id.ToString(), null, new { Kind = incident.Kind.ToString(), CommentLength = text.Length }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Every blocklist of the catalog with its state.</summary>
    public async Task<IReadOnlyList<IpFeedView>> ListFeedsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.SecurityView);
        await using var db = _dbFactory.CreateSystem();
        var rows = await db.IpFeeds.AsNoTracking().ToDictionaryAsync(f => f.Key, cancellationToken);
        var crowdSec = await _settings.GetAsync<CrowdSecFeedSettings>(SettingKeys.CrowdSecFeed, cancellationToken);
        return IpFeeds.All.Select(info =>
        {
            var row = rows.GetValueOrDefault(info.Key);
            return new IpFeedView(info.Key, info.Name, info.Description, info.TermsUrl, row?.Enabled ?? false,
                info.Key != IpFeeds.CrowdSec || crowdSec is not null, row?.EntryCount ?? 0, row?.LastAttemptAt, row?.LastSuccessAt, row?.Problem,
                row?.RefreshRequestedAt is not null, info.Key == IpFeeds.CrowdSec ? crowdSec?.IntegrationId : null);
        }).ToList();
    }

    /// <summary>Switches a blocklist on or off. Off, its entries no longer block and the workers empty it.</summary>
    public async Task<ServiceResult> SetFeedEnabledAsync(Caller caller, string key, bool on, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        if (IpFeeds.Find(key) is null)
        {
            return ServiceResult.NotFound("blocklist");
        }

        if (on && key == IpFeeds.CrowdSec && await _settings.GetAsync<CrowdSecFeedSettings>(SettingKeys.CrowdSecFeed, cancellationToken) is null)
        {
            return ServiceResult.Fail("Enter the CrowdSec integration first: its id, user name and password from the CrowdSec Console.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var feed = await db.IpFeeds.SingleOrDefaultAsync(f => f.Key == key, cancellationToken);
        if (feed is null)
        {
            return ServiceResult.NotFound("blocklist");
        }

        if (feed.Enabled == on)
        {
            return ServiceResult.Ok();
        }

        feed.Enabled = on;
        // Switched on: fetched at once rather than in up to six hours.
        feed.RefreshRequestedAt = on ? now : null;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpFeedChanged, "IpFeed", key, null, new { Enabled = on }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Asks the workers to fetch a blocklist that is on, now.</summary>
    public async Task<ServiceResult> RefreshFeedAsync(Caller caller, string key, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var feed = await db.IpFeeds.SingleOrDefaultAsync(f => f.Key == key, cancellationToken);
        if (feed is null)
        {
            return ServiceResult.NotFound("blocklist");
        }

        if (!feed.Enabled)
        {
            return ServiceResult.Fail("Switch the blocklist on first.");
        }

        feed.RefreshRequestedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpFeedChanged, "IpFeed", key, null, new { Refresh = true }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Sets the CrowdSec Console firewall integration: its id and the Basic Auth credentials the Console shows once. Stored
    /// encrypted; the password is never shown again and never audited. The address is fixed, so only CrowdSec is ever called.
    /// </summary>
    public async Task<ServiceResult> SetCrowdSecAsync(Caller caller, string? integrationId, string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        var id = integrationId?.Trim() ?? string.Empty;
        var user = username?.Trim() ?? string.Empty;
        if (id.Length is 0 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return ServiceResult.Fail("Enter the integration id from the CrowdSec Console: letters, digits and dashes, as in its endpoint URL.");
        }

        if (user.Length is 0 or > 200 || string.IsNullOrEmpty(password) || password.Length > 500)
        {
            return ServiceResult.Fail("Enter the user name and password the CrowdSec Console showed when the integration was created.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _settings.SetAsync(SettingKeys.CrowdSecFeed, new CrowdSecFeedSettings(id, user, password), encrypted: true, caller.UserId, cancellationToken);
        await using var db = _dbFactory.CreateSystem();
        var feed = await db.IpFeeds.SingleAsync(f => f.Key == IpFeeds.CrowdSec, cancellationToken);
        feed.Enabled = true;
        feed.RefreshRequestedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpFeedChanged, "IpFeed", IpFeeds.CrowdSec, null,
            new { Enabled = true, IntegrationId = id, CredentialsChanged = true }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Removes the CrowdSec integration and switches the list off.</summary>
    public async Task<ServiceResult> RemoveCrowdSecAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        await db.Settings.Where(s => s.Key == SettingKeys.CrowdSecFeed).ExecuteDeleteAsync(cancellationToken);
        await db.IpFeeds.Where(f => f.Key == IpFeeds.CrowdSec).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.Enabled, false).SetProperty(f => f.RefreshRequestedAt, (DateTime?)null).SetProperty(f => f.Problem, (string?)null), cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpFeedChanged, "IpFeed", IpFeeds.CrowdSec, null,
            new { Enabled = false, IntegrationRemoved = true }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<SecurityMonitorSettings> GetSettingsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.SecurityView);
        return await _settings.GetAsync<SecurityMonitorSettings>(SettingKeys.SecurityMonitor, cancellationToken) ?? new SecurityMonitorSettings();
    }

    /// <summary>Switches automatic blocking on or off. Blocks that exist stay until they end or are lifted.</summary>
    public async Task<ServiceResult> SetAutomaticBlockingAsync(Caller caller, bool on, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        var current = await _settings.GetAsync<SecurityMonitorSettings>(SettingKeys.SecurityMonitor, cancellationToken) ?? new SecurityMonitorSettings();
        if (current.AutomaticBlocking == on)
        {
            return ServiceResult.Ok();
        }

        await _settings.SetAsync(SettingKeys.SecurityMonitor, current with { AutomaticBlocking = on }, encrypted: false, caller.UserId, cancellationToken);
        await using var db = _dbFactory.CreateSystem();
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SecuritySettingsChanged, "Settings", SettingKeys.SecurityMonitor, null,
            new { AutomaticBlocking = on }), _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Every block, active first, then the most recent; expired automatic blocks stay listed until retention removes them.</summary>
    public async Task<IReadOnlyList<IpBlockView>> ListBlocksAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.SecurityView);
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var blocks = await db.IpBlocks.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(1000).ToListAsync(cancellationToken);
        return blocks
            .Select(b => new IpBlockView(b.Id, b.Network, b.Source, b.Reason, b.CreatedAt, b.ExpiresAt, b.CreatedByName, b.ExpiresAt is null || b.ExpiresAt > now))
            .OrderByDescending(b => b.Active)
            .ThenByDescending(b => b.CreatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<IpAllowView>> ListAllowsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.SecurityView);
        await using var db = _dbFactory.CreateSystem();
        return await db.IpAllows.AsNoTracking().OrderBy(a => a.Network)
            .Select(a => new IpAllowView(a.Id, a.Network, a.Description, a.CreatedAt, a.CreatedByName))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Blocks an address or network by hand. Refused for a range that may never be blocked, a network wider than
    /// <see cref="IpNetworks.MinIPv4Prefix"/> or <see cref="IpNetworks.MinIPv6Prefix"/>, and a network that holds the caller's own
    /// address, so nobody locks themselves out. An address on the allowlist stays allowed.
    /// </summary>
    public async Task<ServiceResult> BlockAsync(Caller caller, string? network, string? reason, TimeSpan? duration,
        CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        if (ValidateNetwork(network, out var parsed) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        if (IpNetworks.TouchesReserved(parsed))
        {
            return ServiceResult.Fail("This range holds private or reserved addresses, which the instance itself uses. Fleeto never blocks them.");
        }

        if (caller.IpAddress is { } own && IPAddress.TryParse(own, out var ownAddress) && IpNetworks.Contains(parsed, ownAddress))
        {
            return ServiceResult.Fail($"This would block your own address ({own}) and sign you out. Block a narrower network, or ask another admin.");
        }

        var cleanReason = reason?.Trim() ?? string.Empty;
        if (cleanReason.Length is 0 or > MaxReasonLength)
        {
            return ServiceResult.Fail($"Enter why the address is blocked, in at most {MaxReasonLength} characters.");
        }

        if (duration is { } length && (length <= TimeSpan.Zero || length > MaxBlockDuration))
        {
            return ServiceResult.Fail("Choose how long the address stays blocked: at most a year, or until an admin lifts it.");
        }

        var now = _time.GetUtcNow().UtcDateTime;

        var text = IpNetworks.Format(parsed);
        await using var db = _dbFactory.CreateSystem();
        if (await db.IpBlocks.AnyAsync(b => b.Network == text, cancellationToken))
        {
            return ServiceResult.Fail($"{text} is blocked already. Lift that block first to change it.");
        }

        var block = new IpBlock
        {
            Id = Guid.NewGuid(),
            Network = text,
            Source = IpBlockSource.Manual,
            Reason = cleanReason,
            CreatedAt = now,
            ExpiresAt = duration is { } blockFor ? now + blockFor : null,
            CreatedById = caller.UserId,
            CreatedByName = caller.Name
        };
        db.IpBlocks.Add(block);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpBlockCreated, "IpBlock", block.Id.ToString(), null,
            new { Network = text, Reason = cleanReason, block.ExpiresAt }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult.Fail($"{text} is blocked already. Lift that block first to change it.");
        }

        return ServiceResult.Ok();
    }

    /// <summary>Lifts a block, manual or automatic.</summary>
    public async Task<ServiceResult> RemoveBlockAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var block = await db.IpBlocks.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (block is null)
        {
            return ServiceResult.NotFound("block");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.IpBlocks.Remove(block);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpBlockRemoved, "IpBlock", block.Id.ToString(), null,
            new { block.Network, Source = block.Source.ToString(), block.Reason }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Allows an address or network: never blocked, by an admin, the security monitor or a blocklist.</summary>
    public async Task<ServiceResult> AllowAsync(Caller caller, string? network, string? description, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        if (ValidateNetwork(network, out var parsed) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        if (IpNetworks.TouchesReserved(parsed))
        {
            return ServiceResult.Fail("This range holds private or reserved addresses, which Fleeto never blocks. Allowing it changes nothing.");
        }

        var cleanDescription = description?.Trim() ?? string.Empty;
        if (cleanDescription.Length is 0 or > MaxReasonLength)
        {
            return ServiceResult.Fail($"Describe whose address this is, in at most {MaxReasonLength} characters.");
        }

        var text = IpNetworks.Format(parsed);
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        if (await db.IpAllows.AnyAsync(a => a.Network == text, cancellationToken))
        {
            return ServiceResult.Fail($"{text} is allowed already.");
        }

        var allow = new IpAllow
        {
            Id = Guid.NewGuid(),
            Network = text,
            Description = cleanDescription,
            CreatedAt = now,
            CreatedById = caller.UserId,
            CreatedByName = caller.Name
        };
        db.IpAllows.Add(allow);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpAllowCreated, "IpAllow", allow.Id.ToString(), null,
            new { Network = text, Description = cleanDescription }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return ServiceResult.Fail($"{text} is allowed already.");
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RemoveAllowAsync(Caller caller, Guid id, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.SecurityManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var allow = await db.IpAllows.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (allow is null)
        {
            return ServiceResult.NotFound("allowed address");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.IpAllows.Remove(allow);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.IpAllowRemoved, "IpAllow", allow.Id.ToString(), null,
            new { allow.Network, allow.Description }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static string? ValidateNetwork(string? text, out IPNetwork network)
    {
        if (!IpNetworks.TryParse(text, out network))
        {
            return "Enter an IP address (203.0.113.7) or a network in CIDR notation (203.0.113.0/24, 2001:db8::/48).";
        }

        return IpNetworks.IsTooWide(network)
            ? $"This network is too wide. Use at least /{IpNetworks.MinIPv4Prefix} for IPv4 and /{IpNetworks.MinIPv6Prefix} for IPv6."
            : null;
    }
}
