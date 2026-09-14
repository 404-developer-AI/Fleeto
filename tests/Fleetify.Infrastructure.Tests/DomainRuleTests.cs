using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Security;

namespace Fleetify.Infrastructure.Tests;

/// <summary>
/// Pure domain rules (0.1.0): license states with the 14-day warning and grace period, effective tier after expiry,
/// license documents that verify only for the right key and FQDN, and check evaluation by the server.
/// </summary>
public class DomainRuleTests
{
    private static readonly DateTime Expiry = new(2027, 1, 31, 23, 59, 59, DateTimeKind.Utc);

    [Theory]
    [InlineData(-30, LicenseState.Valid)]
    [InlineData(-14, LicenseState.ExpiringSoon)]
    [InlineData(-1, LicenseState.ExpiringSoon)]
    [InlineData(0, LicenseState.GracePeriod)]
    [InlineData(13, LicenseState.GracePeriod)]
    [InlineData(14, LicenseState.Expired)]
    [InlineData(400, LicenseState.Expired)]
    public void License_state_follows_warning_and_grace_windows(int daysFromExpiry, LicenseState expected)
    {
        var status = LicenseStatus.Evaluate(10, Expiry, Expiry.AddDays(daysFromExpiry));

        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void Managed_behaviour_continues_during_grace_and_stops_after_it()
    {
        var grace = LicenseStatus.Evaluate(10, Expiry, Expiry.AddDays(3));
        var expired = LicenseStatus.Evaluate(10, Expiry, Expiry.AddDays(15));

        Assert.Equal(EndpointTier.Managed, TierRules.EffectiveTier(EndpointTier.Managed, grace));
        Assert.Equal(10, grace.Capacity);
        Assert.Equal(EndpointTier.AgentOnly, TierRules.EffectiveTier(EndpointTier.Managed, expired));
        Assert.Equal(0, expired.Capacity);
        Assert.Equal(EndpointTier.AgentOnly, TierRules.EffectiveTier(EndpointTier.Managed, LicenseStatus.None));
    }

    [Fact]
    public void Tier_rules_refuse_managed_features_on_agent_only_endpoints()
    {
        var endpoint = new Endpoint { Id = Guid.NewGuid(), Tier = EndpointTier.AgentOnly };

        var error = Assert.Throws<TierRequiredException>(() => TierRules.EnsureManaged(endpoint, ManagedFeature.RemoteControl));
        Assert.Contains("managed", error.Message);

        endpoint.Tier = EndpointTier.Managed;
        TierRules.EnsureManaged(endpoint, ManagedFeature.RemoteControl);
    }

    [Fact]
    public void License_verifies_for_its_key_and_fqdn_only()
    {
        var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
        var (_, otherPublicKey) = Ed25519.GenerateKeyPair();
        var text = LicenseCodec.Sign(new LicenseDocument
        {
            Serial = "FL-1",
            CustomerName = "Acme IT",
            Fqdn = "rmm.acme.example",
            ManagedEndpointCount = 50,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddYears(1)
        }, privateKey);

        var valid = LicenseCodec.Verify(text, [publicKey], "RMM.acme.example.");
        Assert.True(valid.IsValid);
        Assert.Equal(50, valid.Document!.ManagedEndpointCount);

        Assert.False(LicenseCodec.Verify(text, [otherPublicKey], "rmm.acme.example").IsValid);
        var wrongFqdn = LicenseCodec.Verify(text, [publicKey], "rmm.other.example");
        Assert.False(wrongFqdn.IsValid);
        Assert.Contains("rmm.other.example", wrongFqdn.Problem);
        Assert.False(LicenseCodec.Verify(text, [], "rmm.acme.example").IsValid);
    }

    [Fact]
    public void Tampered_license_is_refused()
    {
        var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
        var text = LicenseCodec.Sign(new LicenseDocument
        {
            Serial = "FL-2", CustomerName = "Acme", Fqdn = "rmm.acme.example", ManagedEndpointCount = 5,
            IssuedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddYears(1)
        }, privateKey);
        var lines = text.Split('\n');
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[1])).Replace("\"managedEndpointCount\":5", "\"managedEndpointCount\":5000");
        lines[1] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        Assert.False(LicenseCodec.Verify(string.Join('\n', lines), [publicKey], "rmm.acme.example").IsValid);
    }

    [Theory]
    [InlineData(CheckType.CpuUsage, 50d, 80d, 95d, CheckStatus.Ok)]
    [InlineData(CheckType.CpuUsage, 80d, 80d, 95d, CheckStatus.Warning)]
    [InlineData(CheckType.CpuUsage, 99d, 80d, 95d, CheckStatus.Critical)]
    [InlineData(CheckType.DiskFree, 50d, 15d, 5d, CheckStatus.Ok)]
    [InlineData(CheckType.DiskFree, 10d, 15d, 5d, CheckStatus.Warning)]
    [InlineData(CheckType.DiskFree, 5d, 15d, 5d, CheckStatus.Critical)]
    [InlineData(CheckType.Uptime, 45d, 30d, 60d, CheckStatus.Warning)]
    [InlineData(CheckType.ServiceRunning, 1d, null, null, CheckStatus.Ok)]
    [InlineData(CheckType.ServiceRunning, 0d, null, null, CheckStatus.Critical)]
    public void Server_evaluates_values_against_thresholds(CheckType type, double value, double? warning, double? critical, CheckStatus expected)
    {
        Assert.Equal(expected, CheckEvaluator.Evaluate(type, value, null, warning, critical));
    }

    [Fact]
    public void Check_that_could_not_run_is_unknown_and_alert_title_says_so()
    {
        Assert.Equal(CheckStatus.Unknown, CheckEvaluator.Evaluate(CheckType.ServiceRunning, 0, "Service Spooler does not exist", null, null));

        var definition = new CheckDefinition { Name = "Print spooler", Type = CheckType.ServiceRunning, ParametersJson = """{"service":"Spooler"}""" };
        Assert.Contains("could not run", CheckEvaluator.AlertTitle("WS-01", definition, "", CheckStatus.Unknown, 0, "x"));
        Assert.Equal("Service Spooler is not running on WS-01.", CheckEvaluator.AlertTitle("WS-01", definition, "", CheckStatus.Critical, 0, null));
    }

    [Fact]
    public void Check_definition_validation_explains_problems()
    {
        var disk = new CheckDefinition { Name = "Disk", Type = CheckType.DiskFree, IntervalSeconds = 5, WarningThreshold = 5, CriticalThreshold = 15 };

        var problems = CheckParameters.Validate(disk);

        Assert.Contains(problems, p => p.Contains("interval"));
        Assert.Contains(problems, p => p.Contains("drive"));
        Assert.Contains(problems, p => p.Contains("higher than the critical"));
    }

    [Theory]
    [InlineData("acme", "ACME", null)]
    [InlineData("  ab-12 ", "AB-12", null)]
    [InlineData("-AB", "-AB", "letter or digit")]
    [InlineData("A B", "A B", "letters A to Z")]
    [InlineData("", "", "Enter a client code")]
    public void Client_codes_are_normalized_and_validated(string input, string normalized, string? problem)
    {
        var code = ClientCode.Normalize(input);

        Assert.Equal(normalized, code);
        var result = ClientCode.Validate(code);
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result);
        }
    }

    [Theory]
    [InlineData(30, "every 30 seconds")]
    [InlineData(60, "every minute")]
    [InlineData(300, "every 5 minutes")]
    [InlineData(3600, "every hour")]
    [InlineData(86400, "once a day")]
    [InlineData(2678400, "once a month")]
    public void Intervals_are_plain_language(int seconds, string expected)
    {
        Assert.Equal(expected, Intervals.Describe(seconds));
    }
}
