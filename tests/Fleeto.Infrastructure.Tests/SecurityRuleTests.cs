using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The rules of the security monitor (0.7.0, step 2): every threshold just below and at its value, what asks for a block and
/// what only reports, and how long a repeated block lasts.
/// </summary>
public sealed class SecurityRuleTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private const string Attacker = "81.2.69.160/32";

    private static IReadOnlyList<SecurityFinding> Evaluate(IReadOnlyList<SignalCount>? signals = null, IReadOnlyList<FailedSignIn>? failed = null,
        IReadOnlyList<SecurityAuditEvent>? events = null, IReadOnlyList<FirstSignInFromNetwork>? networks = null, SecurityPosture? posture = null) =>
        SecurityRules.Evaluate(new SecurityEvidence(Now, signals ?? [], failed ?? [], events ?? [], networks ?? [], posture));

    private static SignalCount Signal(SecuritySignalKind kind, int last10, string network = Attacker) => new(kind, network, last10, last10, last10);

    [Theory]
    [InlineData(SecuritySignalKind.SignInFailed, SecurityRules.BruteForceAttempts, SecurityIncidentKind.BruteForce)]
    [InlineData(SecuritySignalKind.ApiKeyInvalid, SecurityRules.ApiKeyFailures, SecurityIncidentKind.ApiKeyMisuse)]
    [InlineData(SecuritySignalKind.TlsHandshakeFailed, SecurityRules.AgentPortFailures, SecurityIncidentKind.AgentPortAttack)]
    [InlineData(SecuritySignalKind.ScannerPath, SecurityRules.ScannerPaths, SecurityIncidentKind.Scanner)]
    [InlineData(SecuritySignalKind.NotFound, SecurityRules.NotFoundRequests, SecurityIncidentKind.Enumeration)]
    [InlineData(SecuritySignalKind.RateLimited, SecurityRules.RateLimitRefusals, SecurityIncidentKind.RateLimitFlood)]
    public void A_burst_from_one_address_is_found_at_its_threshold_and_asks_for_a_block(SecuritySignalKind kind, int threshold, SecurityIncidentKind expected)
    {
        Assert.Empty(Evaluate([Signal(kind, threshold - 1)]));
        var finding = Assert.Single(Evaluate([Signal(kind, threshold)]));
        Assert.Equal((expected, Attacker, true), (finding.Kind, finding.Network, finding.Block));
    }

    [Fact]
    public void Kinds_of_one_burst_add_up_and_other_addresses_do_not()
    {
        var findings = Evaluate([
            Signal(SecuritySignalKind.SignInFailed, 6), Signal(SecuritySignalKind.TwoFactorSetupFailed, 4),
            Signal(SecuritySignalKind.SignInFailed, 9, "81.2.69.161/32")
        ]);
        Assert.Equal(Attacker, Assert.Single(findings).Network);
    }

    [Theory]
    [InlineData(SecuritySignalKind.SensitivePath)]
    [InlineData(SecuritySignalKind.AttackPattern)]
    [InlineData(SecuritySignalKind.ScannerAgent)]
    public void One_certain_probe_is_a_scanner_blocked_for_at_least_a_day(SecuritySignalKind kind)
    {
        var finding = Assert.Single(Evaluate([Signal(kind, 1)]));
        Assert.Equal((SecurityIncidentKind.Scanner, true, TimeSpan.FromHours(24)), (finding.Kind, finding.Block, finding.MinimumBlock));
    }

    [Fact]
    public void A_revoked_certificate_is_critical_and_never_blocks()
    {
        var finding = Assert.Single(Evaluate([new SignalCount(SecuritySignalKind.AgentCertificateRevoked, Attacker, 0, 0, 1)]));
        Assert.Equal((SecurityIncidentKind.RevokedCertificate, AlertSeverity.Critical, false), (finding.Kind, finding.Severity, finding.Block));
    }

    [Fact]
    public void Password_spraying_is_many_accounts_from_one_address_in_half_an_hour()
    {
        FailedSignIn Try(int account, int minutesAgo = 5) => new(Now.AddMinutes(-minutesAgo), Attacker, $"user{account}@example.com", null);
        Assert.Empty(Evaluate(failed: [.. Enumerable.Range(0, SecurityRules.SprayingAccounts - 1).Select(i => Try(i))]));
        Assert.Empty(Evaluate(failed: [.. Enumerable.Range(0, SecurityRules.SprayingAccounts).Select(i => Try(i, minutesAgo: 40))]));

        var finding = Assert.Single(Evaluate(failed: [.. Enumerable.Range(0, SecurityRules.SprayingAccounts).Select(i => Try(i))]));
        Assert.Equal((SecurityIncidentKind.PasswordSpraying, AlertSeverity.Critical, true), (finding.Kind, finding.Severity, finding.Block));
        Assert.DoesNotContain("@example.com", finding.Title + finding.Detail);
    }

    [Fact]
    public void An_attack_on_one_account_from_many_addresses_names_the_user_and_blocks_nobody()
    {
        var spread = Enumerable.Range(0, SecurityRules.AccountAttackAttempts)
            .Select(i => new FailedSignIn(Now.AddMinutes(-i), $"81.2.70.{i % SecurityRules.AccountAttackNetworks}/32", "user-1", "Alex"))
            .ToList();
        var finding = Assert.Single(Evaluate(failed: spread));
        Assert.Equal((SecurityIncidentKind.AccountAttack, false, null), (finding.Kind, finding.Block, finding.Network));
        Assert.Contains("Alex", finding.Title);

        // From one address it is a brute force of that address (found by its signals), not a spread attack on the account.
        Assert.Empty(Evaluate(failed: spread.Select(f => f with { Network = Attacker }).ToList()));
    }

    [Fact]
    public void Privilege_changes_a_locked_admin_mass_jobs_and_signing_refusals_are_reported()
    {
        var events = new List<SecurityAuditEvent>
        {
            new(1, Now.AddMinutes(-5), AuditActions.ApiKeyCreated, "u1", "Sam", Attacker, "k1"),
            new(2, Now.AddMinutes(-5), AuditActions.LockedOut, "u2", "Robin", Attacker, "u2", TargetIsAdmin: true),
            new(3, Now.AddMinutes(-5), AuditActions.LockedOut, "u3", "Kim", Attacker, "u3", TargetIsAdmin: false),
            new(4, Now.AddMinutes(-5), AuditActions.JobBatchStarted, "u1", "Sam", null, "b1", Endpoints: SecurityRules.MassJobEndpoints),
            new(5, Now.AddMinutes(-5), AuditActions.JobBatchStarted, "u1", "Sam", null, "b2", Endpoints: SecurityRules.MassJobEndpoints - 1),
        };
        events.AddRange(Enumerable.Range(10, SecurityRules.SigningRefusals).Select(i =>
            new SecurityAuditEvent(i, Now.AddMinutes(-2), AuditActions.SigningRefused, "u1", "Sam", null, "r")));

        var kinds = Evaluate(events: events).Select(f => f.Kind).OrderBy(k => k).ToList();
        Assert.Equal([SecurityIncidentKind.AdminLockedOut, SecurityIncidentKind.PrivilegeChange, SecurityIncidentKind.MassJobs,
            SecurityIncidentKind.SigningRefusals], kinds);
        Assert.All(Evaluate(events: events), f => Assert.False(f.Block));
    }

    [Fact]
    public void Many_remote_sessions_of_one_user_in_an_hour_are_reported()
    {
        var sessions = Enumerable.Range(0, SecurityRules.MassRemoteSessions)
            .Select(i => new SecurityAuditEvent(i, Now.AddMinutes(-i), AuditActions.RemoteSessionRequested, "u1", "Sam", null, "e")).ToList();
        Assert.Equal(SecurityIncidentKind.MassRemoteSessions, Assert.Single(Evaluate(events: sessions)).Kind);
        Assert.Empty(Evaluate(events: sessions.Skip(1).ToList()));
    }

    [Fact]
    public void A_sign_in_from_a_new_network_and_posture_are_reported_without_a_block()
    {
        var findings = Evaluate(networks: [new FirstSignInFromNetwork(Guid.NewGuid(), "Sam", "81.2.69.0/24", Now)],
            posture: new SecurityPosture(ApiKeysWithoutExpiry: 2, UnusedApiKeys: 0, UnusedAdmins: 1));
        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f.Kind == SecurityIncidentKind.NewSignInNetwork && f.Title.Contains("Sam"));
        Assert.Equal(2, findings.Count(f => f.Kind == SecurityIncidentKind.Posture));
        Assert.All(findings, f => Assert.False(f.Block));
        Assert.Empty(Evaluate(posture: new SecurityPosture(0, 0, 0)));
    }

    [Fact]
    public void A_repeated_block_lasts_an_hour_then_a_day_then_a_week_and_never_forever()
    {
        Assert.Equal(TimeSpan.FromHours(1), SecurityRules.BlockDuration(1));
        Assert.Equal(TimeSpan.FromHours(24), SecurityRules.BlockDuration(2));
        Assert.Equal(TimeSpan.FromDays(7), SecurityRules.BlockDuration(3));
        Assert.Equal(TimeSpan.FromDays(7), SecurityRules.BlockDuration(40));
    }
}
