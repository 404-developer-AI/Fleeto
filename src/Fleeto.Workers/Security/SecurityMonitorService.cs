using System.Net;
using System.Text.Json;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Email;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Security;

/// <summary>
/// The security monitor (0.7.0, step 2, ARCHITECTURE.md §5): every minute it reads the signals of web and the gateway and the last
/// hour of the audit log, judges them with <see cref="SecurityRules"/>, and keeps the security incidents in step: a finding opens
/// one, a worse one escalates it, a repeat counts up, and one that stays away for the quiet period resolves it. A finding that asks
/// for it blocks the attacking network for an hour, then a day, then a week; never an allowed address, a reserved range, a network
/// a user signed in from or a managed endpoint connected from in the last 30 days. Every opened, escalated or resolved incident
/// goes to the notification channels that chose security incidents, in the same transaction, except an attack that is blocked
/// by itself and needs nobody (a scanner, enumeration, a flood): those are on the page, not in every inbox, because every server
/// on the internet is scanned all day.
/// </summary>
public sealed class SecurityMonitorService : WorkerLoop
{
    public const string CategoryOpened = "security_incident.opened";
    public const string CategoryEscalated = "security_incident.escalated";
    public const string CategoryResolved = "security_incident.resolved";

    /// <summary>Resolved incidents are kept this long.</summary>
    public static readonly TimeSpan ResolvedRetention = TimeSpan.FromDays(365);

    private static readonly TimeSpan PostureInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan TrustedInterval = TimeSpan.FromMinutes(10);

    /// <summary>Attacks that handle themselves once blocked: no notification, unless they could not be blocked.</summary>
    private static readonly HashSet<SecurityIncidentKind> Background =
    [
        SecurityIncidentKind.Scanner, SecurityIncidentKind.Enumeration, SecurityIncidentKind.RateLimitFlood, SecurityIncidentKind.AgentPortAttack
    ];

    private static readonly string[] EventActions =
    [
        .. SecurityRules.PrivilegeChanges.Keys, AuditActions.LockedOut, AuditActions.JobBatchStarted, AuditActions.RemoteSessionRequested,
        AuditActions.SigningRefused
    ];

    private const string SignalsSql = """
        SELECT "Kind", "Network",
               COALESCE(sum("Count") FILTER (WHERE "Minute" >= {0}), 0)::int AS "Last10",
               COALESCE(sum("Count") FILTER (WHERE "Minute" >= {1}), 0)::int AS "Last30",
               COALESCE(sum("Count"), 0)::int AS "Hour"
        FROM "SecuritySignals" WHERE "Minute" >= {2}
        GROUP BY "Kind", "Network"
        """;

    // Users who can reach endpoints: admins, and every role with remote control or remote background (the built-in rows included).
    private const string PrivilegedUsersSql = """
        SELECT DISTINCT ur."UserId" AS "Value" FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
        WHERE r."NormalizedName" = 'ADMIN'
           OR EXISTS (SELECT 1 FROM "RolePermissions" p WHERE p."RoleId" = r."Id" AND p."Permission" IN ('remote.control', 'remote.background'))
        """;

    private const string AdminsSql = """
        SELECT ur."UserId" AS "Value" FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId" WHERE r."NormalizedName" = 'ADMIN'
        """;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private SecurityPosture? _posture;
    private DateTimeOffset _nextPosture = DateTimeOffset.MinValue;
    private Trusted? _trusted;
    private DateTimeOffset _nextTrusted = DateTimeOffset.MinValue;

    public SecurityMonitorService(IFleetoDbContextFactory dbFactory, SettingsStore settings, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<SecurityMonitorService> logger)
        : base("security-monitor", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    protected override TimeSpan Interval => SecurityRules.Interval;

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await EvaluateAsync(cancellationToken);
        return false;
    }

    /// <summary>One pass: reads the evidence, applies the findings and the blocks. Returns the findings.</summary>
    public async Task<IReadOnlyList<SecurityFinding>> EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        if (_posture is null || Time.GetUtcNow() >= _nextPosture)
        {
            _posture = await ReadPostureAsync(db, now, cancellationToken);
            _nextPosture = Time.GetUtcNow() + PostureInterval;
        }

        if (_trusted is null || Time.GetUtcNow() >= _nextTrusted)
        {
            _trusted = await ReadTrustedAsync(db, now, cancellationToken);
            _nextTrusted = Time.GetUtcNow() + TrustedInterval;
        }

        var evidence = await ReadEvidenceAsync(db, now, cancellationToken);
        var findings = SecurityRules.Evaluate(evidence);
        var settings = await _settings.GetAsync<SecurityMonitorSettings>(SettingKeys.SecurityMonitor, cancellationToken) ?? new SecurityMonitorSettings();
        await ApplyAsync(db, findings, settings, _trusted, now, cancellationToken);
        return findings;
    }

    /// <summary>Forgets the cached posture and trusted networks, so the next pass reads them again (tests).</summary>
    public void Refresh()
    {
        _posture = null;
        _trusted = null;
    }

    private async Task<SecurityEvidence> ReadEvidenceAsync(FleetoDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var hourAgo = now.AddHours(-1);
        var signals = (await db.Database.SqlQueryRaw<SignalRow>(SignalsSql, now.AddMinutes(-10), now.AddMinutes(-30), hourAgo)
                .ToListAsync(cancellationToken))
            .Where(r => Enum.TryParse<SecuritySignalKind>(r.Kind, out _))
            .Select(r => new SignalCount(Enum.Parse<SecuritySignalKind>(r.Kind), r.Network, r.Last10, r.Last30, r.Hour))
            .ToList();

        var failed = (await db.AuditEntries.AsNoTracking()
                .Where(a => a.Action == AuditActions.LoginFailed && a.Time >= hourAgo && a.IpAddress != null)
                .OrderByDescending(a => a.Time).Take(20_000)
                .Select(a => new { a.Time, a.ActorId, a.ActorName, a.IpAddress })
                .ToListAsync(cancellationToken))
            .Select(a => Network(a.IpAddress) is { } network
                ? new FailedSignIn(a.Time, network, a.ActorId == "unknown" ? a.ActorName : a.ActorId, a.ActorId == "unknown" ? null : a.ActorName)
                : null)
            .OfType<FailedSignIn>()
            .ToList();

        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => EventActions.Contains(a.Action) && a.Time >= hourAgo)
            .OrderBy(a => a.Id).Take(5_000)
            .ToListAsync(cancellationToken);
        var admins = entries.Any(e => e.Action == AuditActions.LockedOut)
            ? (await db.Database.SqlQueryRaw<Guid>(AdminsSql).ToListAsync(cancellationToken)).Select(g => g.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var events = entries.Select(e => new SecurityAuditEvent(e.Id, e.Time, e.Action, e.ActorId, e.ActorName, Network(e.IpAddress), e.TargetId,
                e.Action == AuditActions.JobBatchStarted ? EndpointsOf(e.DetailsJson) : null, admins.Contains(e.TargetId)))
            .ToList();

        var newNetworks = await ReadNewNetworksAsync(db, hourAgo, cancellationToken);
        return new SecurityEvidence(now, signals, failed, events, newNetworks, _posture);
    }

    /// <summary>
    /// Networks a privileged user signed in from for the first time within the hour, while the user had signed in from another
    /// network before: the first sign-in of a new user is not news.
    /// </summary>
    private static async Task<IReadOnlyList<FirstSignInFromNetwork>> ReadNewNetworksAsync(FleetoDbContext db, DateTime since, CancellationToken cancellationToken)
    {
        var recent = await db.UserSignInNetworks.AsNoTracking().Where(n => n.FirstSeen >= since).ToListAsync(cancellationToken);
        if (recent.Count == 0)
        {
            return [];
        }

        var privileged = (await db.Database.SqlQueryRaw<Guid>(PrivilegedUsersSql).ToListAsync(cancellationToken)).ToHashSet();
        var userIds = recent.Select(n => n.UserId).Where(privileged.Contains).Distinct().ToList();
        if (userIds.Count == 0)
        {
            return [];
        }

        var earliest = await db.UserSignInNetworks.AsNoTracking().Where(n => userIds.Contains(n.UserId))
            .GroupBy(n => n.UserId).Select(g => new { UserId = g.Key, First = g.Min(n => n.FirstSeen) })
            .ToDictionaryAsync(g => g.UserId, g => g.First, cancellationToken);
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrEmpty(u.DisplayName) ? u.Email ?? string.Empty : u.DisplayName, cancellationToken);
        return recent.Where(n => userIds.Contains(n.UserId) && earliest[n.UserId] < n.FirstSeen && names.ContainsKey(n.UserId))
            .Select(n => new FirstSignInFromNetwork(n.UserId, names[n.UserId], n.Network, n.FirstSeen))
            .ToList();
    }

    private static async Task<SecurityPosture> ReadPostureAsync(FleetoDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var unusedSince = now.AddDays(-90);
        var keys = db.ApiKeys.AsNoTracking().Where(k => k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now));
        var withoutExpiry = await keys.CountAsync(k => k.ExpiresAt == null, cancellationToken);
        var unusedKeys = await keys.CountAsync(k => (k.LastUsedAt ?? k.CreatedAt) < unusedSince, cancellationToken);
        var admins = await db.Database.SqlQueryRaw<Guid>(AdminsSql).ToListAsync(cancellationToken);
        var unusedAdmins = await db.Users.AsNoTracking()
            .CountAsync(u => admins.Contains(u.Id) && (u.LastLoginAt ?? u.CreatedAt) < unusedSince, cancellationToken);
        return new SecurityPosture(withoutExpiry, unusedKeys, unusedAdmins);
    }

    private static async Task<Trusted> ReadTrustedAsync(FleetoDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var since = now - SecurityRules.TrustedNetworkAge;
        var signIns = await db.UserSignInNetworks.AsNoTracking().Where(n => n.LastSeen >= since).Select(n => n.Network).Distinct()
            .ToListAsync(cancellationToken);
        var agents = await db.Endpoints.AsNoTracking().Where(e => e.PublicIpAddress != null && e.PublicIpSeenAt >= since)
            .Select(e => e.PublicIpAddress!).Distinct().ToListAsync(cancellationToken);
        var allows = await db.IpAllows.AsNoTracking().Select(a => a.Network).ToListAsync(cancellationToken);
        return new Trusted(
            signIns.ToHashSet(StringComparer.Ordinal),
            agents.Select(Network).OfType<string>().ToHashSet(StringComparer.Ordinal),
            allows.Select(a => IpNetworks.TryParse(a, out var n) ? n : (IPNetwork?)null).OfType<IPNetwork>().ToList());
    }

    /// <summary>Why a network may not be blocked automatically, or null when it may.</summary>
    internal static string? WhyNotBlocked(string network, Trusted trusted)
    {
        if (!IpNetworks.TryParse(network, out var parsed))
        {
            return "the address could not be read";
        }

        if (IpNetworks.TouchesReserved(parsed))
        {
            return "it is a private or reserved address";
        }

        if (trusted.Allows.Any(a => IpNetworks.Overlaps(a, parsed)))
        {
            return "it is an allowed address";
        }

        if (trusted.SignInNetworks.Contains(IpNetworks.Format(IpNetworks.SignInNetwork(parsed.BaseAddress))))
        {
            return "a user signed in from its network in the last 30 days";
        }

        return trusted.AgentNetworks.Contains(network) ? "a managed endpoint connected from it in the last 30 days" : null;
    }

    private async Task ApplyAsync(FleetoDbContext db, IReadOnlyList<SecurityFinding> findings, SecurityMonitorSettings settings, Trusted trusted,
        DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var open = await db.SecurityIncidents.Where(i => i.ResolvedAt == null).ToListAsync(cancellationToken);
        var transitions = new List<(SecurityIncident Incident, NotificationEvent Event, bool Handled)>();
        var blockedNow = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            var (note, until, handled) = finding is { Block: true, Network: { } network }
                ? await BlockAsync(db, finding, network, settings, trusted, blockedNow, now, cancellationToken)
                : (null, (DateTime?)null, false);
            var detail = note is null ? finding.Detail : $"{finding.Detail} {note}";

            var incident = open.FirstOrDefault(i => i.Key == finding.Key);
            if (incident is null)
            {
                incident = new SecurityIncident
                {
                    Id = Guid.NewGuid(),
                    Key = Truncate(finding.Key, 200),
                    Kind = finding.Kind,
                    Severity = finding.Severity,
                    Title = Truncate(finding.Title, 500),
                    Detail = Truncate(detail, 2000),
                    Network = finding.Network,
                    Count = finding.Count,
                    OpenedAt = now,
                    LastSeenAt = now,
                    BlockedUntil = until
                };
                db.SecurityIncidents.Add(incident);
                open.Add(incident);
                transitions.Add((incident, NotificationEvent.Opened, handled));
                continue;
            }

            if (finding.Severity > incident.Severity)
            {
                transitions.Add((incident, NotificationEvent.Escalated, handled));
                incident.Severity = finding.Severity;
            }

            incident.Title = Truncate(finding.Title, 500);
            incident.Detail = Truncate(detail, 2000);
            incident.Count = Math.Max(incident.Count, finding.Count);
            incident.LastSeenAt = now;
            incident.BlockedUntil = until ?? incident.BlockedUntil;
        }

        foreach (var incident in open.Where(i => i.ResolvedAt is null && findings.All(f => f.Key != i.Key) &&
                                                 now - i.LastSeenAt >= SecurityRules.QuietPeriodOf(i.Kind)))
        {
            incident.ResolvedAt = now;
            transitions.Add((incident, NotificationEvent.Resolved, Background.Contains(incident.Kind) && incident.BlockedUntil is not null));
        }

        await db.SaveChangesAsync(cancellationToken);
        var notify = transitions.Where(t => !t.Handled).Select(t => (t.Incident, t.Event)).ToList();
        if (notify.Count > 0)
        {
            await AddNotificationsAsync(db, notify, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var (incident, notificationEvent, _) in transitions)
        {
            Logger.LogWarning("Security incident {Event}: {Kind} ({Severity})", notificationEvent, incident.Kind, incident.Severity);
        }
    }

    /// <summary>
    /// Blocks the network of a finding when it may be, or explains why not. Returns the sentence for the incident, the end of the
    /// block, and whether the attack is handled (blocked by Fleeto, so a background attack needs no notification).
    /// </summary>
    private async Task<(string? Note, DateTime? Until, bool Handled)> BlockAsync(FleetoDbContext db, SecurityFinding finding, string network,
        SecurityMonitorSettings settings, Trusted trusted, Dictionary<string, DateTime?> blockedNow, DateTime now, CancellationToken cancellationToken)
    {
        var background = Background.Contains(finding.Kind);
        if (blockedNow.TryGetValue(network, out var already))
        {
            return (Until(already), already, background);
        }

        var existing = await db.IpBlocks.SingleOrDefaultAsync(b => b.Network == network, cancellationToken);
        if (existing is not null && (existing.ExpiresAt is null || existing.ExpiresAt > now))
        {
            blockedNow[network] = existing.ExpiresAt;
            return (Until(existing.ExpiresAt), existing.ExpiresAt, background);
        }

        if (WhyNotBlocked(network, trusted) is { } reason)
        {
            return ($"Not blocked: {reason}.", null, false);
        }

        if (!settings.AutomaticBlocking)
        {
            return ("Not blocked: automatic blocking is off in Settings, Security. Block the address there if needed.", null, false);
        }

        if (await db.IpBlocks.CountAsync(b => b.Source == IpBlockSource.Automatic && b.ExpiresAt > now, cancellationToken) >= SecurityRules.MaxAutomaticBlocks)
        {
            return ($"Not blocked: {SecurityRules.MaxAutomaticBlocks} addresses are blocked automatically already.", null, false);
        }

        var times = existing is not null && existing.Source == IpBlockSource.Automatic && now - existing.CreatedAt <= SecurityRules.RepeatWindow
            ? existing.Times + 1
            : 1;
        var duration = SecurityRules.BlockDuration(times);
        if (finding.MinimumBlock is { } minimum && minimum > duration)
        {
            duration = minimum;
        }

        var until = now + duration;
        var reason2 = Truncate(finding.Title, 500);
        if (existing is null)
        {
            existing = new IpBlock { Id = Guid.NewGuid(), Network = network, Source = IpBlockSource.Automatic, CreatedByName = "Security monitor" };
            db.IpBlocks.Add(existing);
        }

        existing.Source = IpBlockSource.Automatic;
        existing.Reason = reason2;
        existing.CreatedAt = now;
        existing.ExpiresAt = until;
        existing.CreatedById = null;
        existing.CreatedByName = "Security monitor";
        existing.Times = times;
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.IpBlockCreated, "IpBlock", existing.Id.ToString(), null,
            AuditActorType.System, "security-monitor", "Security monitor",
            new { Network = network, Reason = reason2, ExpiresAt = until, Times = times, Kind = finding.Kind.ToString() }), now));
        blockedNow[network] = until;
        return (Until(until), until, background);
    }

    private static string Until(DateTime? until) =>
        until is { } at ? $"Fleeto blocked the address until {at:yyyy-MM-dd HH:mm} UTC." : "The address is blocked until an admin lifts the block.";

    /// <summary>
    /// Queues an email per recipient and a webhook per channel that receives security incidents: switched on for it by an admin,
    /// enabled, at least its minimum severity, and for a resolve only when it wants resolves. Never combined into a digest.
    /// </summary>
    internal static async Task<int> AddNotificationsAsync(FleetoDbContext db, IReadOnlyList<(SecurityIncident Incident, NotificationEvent Event)> transitions,
        DateTime now, CancellationToken cancellationToken)
    {
        var channels = await db.NotificationChannels.AsNoTracking().Where(c => c.Enabled && c.SecurityIncidents).ToListAsync(cancellationToken);
        if (channels.Count == 0)
        {
            return 0;
        }

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var added = 0;
        foreach (var (incident, notificationEvent) in transitions)
        {
            var receiving = channels.Where(c => c.MinimumSeverity <= incident.Severity &&
                                                (notificationEvent != NotificationEvent.Resolved || c.NotifyOnResolve)).ToList();
            var category = notificationEvent switch
            {
                NotificationEvent.Escalated => CategoryEscalated,
                NotificationEvent.Resolved => CategoryResolved,
                _ => CategoryOpened
            };

            foreach (var channel in receiving.Where(c => c is { Type: NotificationChannelType.Webhook, WebhookFormat: not null }))
            {
                var id = Guid.NewGuid();
                db.OutboxWebhooks.Add(new OutboxWebhook
                {
                    Id = id,
                    NotificationChannelId = channel.Id,
                    Category = category,
                    Payload = WebhookPayloads.SecurityIncident(channel.WebhookFormat!.Value, id, instance.Fqdn, notificationEvent, incident.Id,
                        incident.Kind.ToString(), incident.Severity, incident.Title, incident.Detail, incident.Network, incident.OpenedAt, incident.ResolvedAt,
                        instance.SecurityUrl, now),
                    NextAttemptAt = now,
                    CreatedAt = now
                });
                added++;
            }

            var recipients = receiving.Where(c => c.Type == NotificationChannelType.Email)
                .SelectMany(c => EmailAddresses.Split(c.Recipients))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            var content = EmailTemplates.SecurityIncident(instance.Fqdn, notificationEvent, incident.Severity.ToString(), incident.Title, incident.Detail,
                instance.SecurityUrl);
            foreach (var recipient in recipients)
            {
                db.OutboxEmails.Add(OutboxEmails.Create(recipient, content, category, now));
                added++;
            }
        }

        return added;
    }

    private static string? Network(string? address) =>
        IPAddress.TryParse(address, out var parsed) ? IpNetworks.Format(IpNetworks.AttackerNetwork(parsed)) : null;

    private static int? EndpointsOf(string detailsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.TryGetProperty("Endpoints", out var value) && value.TryGetInt32(out var count) ? count : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>What is never blocked automatically: networks users signed in from, networks of managed endpoints, allowed addresses.</summary>
    internal sealed record Trusted(HashSet<string> SignInNetworks, HashSet<string> AgentNetworks, IReadOnlyList<IPNetwork> Allows);

    private sealed class SignalRow
    {
        public string Kind { get; set; } = string.Empty;
        public string Network { get; set; } = string.Empty;
        public int Last10 { get; set; }
        public int Last30 { get; set; }
        public int Hour { get; set; }
    }
}
