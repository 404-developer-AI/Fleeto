using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProtoCheckType = Fleeto.Protocol.Agent.V1.CheckType;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees the check catalog: every check type is described and reaches the agent protocol, parameters that end up in a signed
/// configuration are validated strictly, values are judged per threshold kind, and a check never reaches an endpoint whose
/// platform cannot run it, in C# and in SQL alike.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CheckCatalogTests
{
    // Asks the database whether check @checkId applies to endpoint @endpointId, by the SQL twin of the C# rule.
    private static readonly string AppliesQuerySql =
        """SELECT EXISTS (SELECT 1 FROM "CheckDefinitions" d JOIN "Endpoints" e ON e."Id" = @endpointId WHERE d."Id" = @checkId AND """ +
        EffectiveCheckResolver.AppliesSql + """) AS "Value" """;

    private readonly TestDatabase _db;

    public CheckCatalogTests(DatabaseFixture fixture) => _db = fixture.Database;

    private static Dictionary<string, string> P(params (string Key, string Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    private static CheckDefinition Definition(CheckType type, Dictionary<string, string> parameters, double? warning = null, double? critical = null) => new()
    {
        Name = "Check",
        Type = type,
        IntervalSeconds = 300,
        FailuresBeforeAlert = 1,
        ParametersJson = CheckParameters.Serialize(CheckCatalog.CleanParameters(type, parameters)),
        WarningThreshold = warning,
        CriticalThreshold = critical
    };

    [Fact]
    public void Every_check_type_is_in_the_catalog_and_in_the_agent_protocol()
    {
        foreach (var type in Enum.GetValues<CheckType>())
        {
            var info = CheckCatalog.Get(type);
            Assert.False(string.IsNullOrWhiteSpace(info.Label));
            Assert.NotEqual(ProtoCheckType.Unspecified, AgentConfigBuilder.ToProto(type));
            Assert.All(info.Parameters.Where(p => p.Kind == ParameterKind.Choice), p => Assert.NotEmpty(p.Choices!));
        }

        Assert.Equal(Enum.GetValues<CheckType>().Length, Enum.GetValues<ProtoCheckType>().Length - 1);
    }

    [Fact]
    public void The_defaults_of_every_check_type_are_valid_once_its_required_values_are_filled_in()
    {
        var required = new Dictionary<string, string>
        {
            ["service"] = "Spooler", ["host"] = "10.0.0.1", ["port"] = "443", ["url"] = "https://intranet.example/health",
            ["process"] = "sqlservr.exe", ["path"] = @"D:\Backups", ["script"] = Guid.NewGuid().ToString("D")
        };

        foreach (var info in CheckCatalog.All)
        {
            var parameters = info.Parameters.Where(p => p.Default is not null).ToDictionary(p => p.Name, p => p.Default!);
            foreach (var spec in info.Parameters.Where(p => p.Required && !parameters.ContainsKey(p.Name) && required.ContainsKey(p.Name)))
            {
                parameters[spec.Name] = required[spec.Name];
            }

            var definition = Definition(info.Type, parameters, info.DefaultWarning, info.DefaultCritical);
            if (info.ThresholdKindFor(parameters) is ThresholdKind.HigherIsWorse or ThresholdKind.LowerIsWorse && info.DefaultWarning is null && info.DefaultCritical is null)
            {
                definition.WarningThreshold = 1;
            }

            Assert.True(CheckParameters.Validate(definition).Count == 0,
                $"{info.Type}: {string.Join(" ", CheckParameters.Validate(definition))}");
        }
    }

    [Theory]
    [InlineData(CheckType.Ping, "host", "10.0.0.1; rm -rf /", "host name or IP address")]
    [InlineData(CheckType.Ping, "host", "-oProxyCommand=x", "host name or IP address")]
    [InlineData(CheckType.Http, "url", "ftp://files.example", "http:// or https://")]
    [InlineData(CheckType.Http, "url", "https://admin:secret@intranet.example", "user name or password")]
    [InlineData(CheckType.Http, "expected_status", "200-199", "lowest code")]
    [InlineData(CheckType.TcpPort, "port", "70000", "whole number")]
    [InlineData(CheckType.File, "path", @"relative\path", "full path")]
    [InlineData(CheckType.File, "condition", "deleted", "options")]
    [InlineData(CheckType.EventLog, "event_ids", "7031, 7034", "event ids")]
    [InlineData(CheckType.EventLog, "log", "System' or 1=1", "not allowed")]
    [InlineData(CheckType.EventLog, "source", "Service]", "not allowed")]
    [InlineData(CheckType.ProcessRunning, "process", @"C:\Windows\explorer.exe", "without a folder")]
    [InlineData(CheckType.CertificateExpiry, "store", @"CurrentUser\My", "LocalMachine")]
    public void Unsafe_or_invalid_parameters_are_refused(CheckType type, string name, string value, string expected)
    {
        var info = CheckCatalog.Get(type);
        var parameters = info.Parameters.Where(p => p.Default is not null).ToDictionary(p => p.Name, p => p.Default!);
        parameters["host"] = "10.0.0.1";
        parameters["port"] = "443";
        parameters["url"] = "https://intranet.example";
        parameters["path"] = @"D:\Backups";
        parameters["process"] = "nginx";
        parameters[name] = value;

        var problems = CheckParameters.Validate(Definition(type, parameters, info.DefaultWarning ?? 1, info.DefaultCritical));

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parameters_that_do_not_apply_are_dropped_before_saving()
    {
        var cleaned = CheckCatalog.CleanParameters(CheckType.CertificateExpiry,
            P(("location", "path"), ("store", @"LocalMachine\My"), ("path", "/etc/ssl/certs"), ("unknown", "x"), ("subject", "  ")));

        Assert.Equal(P(("location", "path"), ("path", "/etc/ssl/certs")), cleaned);
    }

    [Theory]
    [InlineData(CheckType.ServiceRunning, "", 0, null, null, CheckStatus.Critical)]
    [InlineData(CheckType.ProcessRunning, "", 0, 1d, null, CheckStatus.Warning)]
    [InlineData(CheckType.ProcessRunning, "", 3, null, null, CheckStatus.Ok)]
    [InlineData(CheckType.PendingReboot, "", 0, 1d, null, CheckStatus.Warning)]
    [InlineData(CheckType.Ping, "10.0.0.1", -1, 100d, 500d, CheckStatus.Critical)]
    [InlineData(CheckType.Ping, "10.0.0.1", 12, null, null, CheckStatus.Ok)]
    [InlineData(CheckType.TcpPort, "db:5432", 150, 100d, 500d, CheckStatus.Warning)]
    [InlineData(CheckType.Http, "", -1, null, null, CheckStatus.Critical)]
    [InlineData(CheckType.Http, "certificate", 10, null, null, CheckStatus.Warning)]
    [InlineData(CheckType.Http, "certificate", 2, null, null, CheckStatus.Critical)]
    [InlineData(CheckType.Http, "certificate", 90, null, null, CheckStatus.Ok)]
    [InlineData(CheckType.CertificateExpiry, "web01", -3, 30d, null, CheckStatus.Critical)]
    [InlineData(CheckType.CertificateExpiry, "web01", 20, 30d, 7d, CheckStatus.Warning)]
    [InlineData(CheckType.EventLog, "", 2, 1d, 10d, CheckStatus.Warning)]
    [InlineData(CheckType.EventLog, "", 0, 1d, 10d, CheckStatus.Ok)]
    [InlineData(CheckType.SecurityCenter, "Microsoft Defender Antivirus", 0, null, null, CheckStatus.Critical)]
    public void Values_are_judged_by_the_threshold_kind_of_their_type(CheckType type, string target, double value, double? warning, double? critical,
        CheckStatus expected)
    {
        Assert.Equal(expected, CheckEvaluator.Evaluate(type, target, value, null, warning, critical, new Dictionary<string, string>()));
    }

    [Fact]
    public void The_file_check_is_a_flag_for_existence_and_a_threshold_for_size_and_age()
    {
        Assert.Equal(CheckStatus.Critical, CheckEvaluator.Evaluate(CheckType.File, "", 0, null, null, null, P(("condition", "exists"))));
        Assert.Equal(CheckStatus.Ok, CheckEvaluator.Evaluate(CheckType.File, "", 1, null, null, null, P(("condition", "missing"))));
        Assert.Equal(CheckStatus.Warning, CheckEvaluator.Evaluate(CheckType.File, "", 30, null, 26, 48, P(("condition", "age"))));
        Assert.Equal(CheckStatus.Critical, CheckEvaluator.Evaluate(CheckType.File, "", 900, null, 500, 800, P(("condition", "size"))));
        Assert.Equal(CheckStatus.Ok, CheckEvaluator.Evaluate(CheckType.Http, "certificate", 5, null, null, null, P(("certificate_warning_days", "0"),
            ("certificate_critical_days", "0"))));

        var age = Definition(CheckType.File, P(("path", "/var/backups/db.dump"), ("condition", "age")), 26, 48);
        Assert.Equal("/var/backups/db.dump on SRV-DB last changed 30 hours ago, above the 26-hour threshold. Check the job that writes it.",
            CheckEvaluator.AlertTitle("SRV-DB", age, "/var/backups/db.dump", CheckStatus.Warning, 30, null));
        Assert.Contains("does not answer ping", CheckEvaluator.AlertTitle("WS-01", Definition(CheckType.Ping, P(("host", "10.0.0.1"))), "10.0.0.1",
            CheckStatus.Critical, -1, null));
    }

    [Fact]
    public void A_script_check_is_judged_by_its_exit_code_and_has_no_thresholds()
    {
        var parameters = P(("script", Guid.NewGuid().ToString("D")));
        Assert.Equal(CheckStatus.Ok, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, 0, null, null, null, parameters));
        Assert.Equal(CheckStatus.Warning, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, 1, null, null, null, parameters));
        Assert.Equal(CheckStatus.Critical, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, 2, null, null, null, parameters));
        Assert.Equal(CheckStatus.Critical, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, -1073741510, null, null, null, parameters));
        Assert.Equal(CheckStatus.Unknown, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, 0, "the script did not finish", null, null, parameters));

        Assert.Contains(CheckParameters.Validate(Definition(CheckType.Script, parameters, 1)), p => p.Contains("no thresholds"));
        var tooOften = Definition(CheckType.Script, parameters);
        tooOften.IntervalSeconds = 30;
        Assert.Contains(CheckParameters.Validate(tooOften), p => p.Contains("once a minute"));
        Assert.Contains(CheckParameters.Validate(Definition(CheckType.Script, P(("script", "../../etc/passwd")))), p => p.Contains("Choose a script"));

        var title = CheckEvaluator.AlertTitle("SRV-01", Definition(CheckType.Script, parameters), string.Empty, CheckStatus.Critical, 3, null);
        Assert.Equal("Script check \"Check\" reports a problem on SRV-01 (exit code 3). Review the detail on the endpoint page.", title);
    }

    [Fact]
    public async Task A_check_type_reaches_only_the_platforms_that_run_it_in_csharp_and_sql()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var windows = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-PLATFORM");
        var linux = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-PLATFORM", EndpointClass.Server);
        var mac = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "MAC-PLATFORM");
        var now = DateTime.UtcNow;
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Platforms " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        CheckDefinition Add(CheckType type, string parameters, double? warning = null) => new()
        {
            Id = Guid.NewGuid(), MonitoringTemplateId = template.Id, Name = type.ToString(), Type = type, ParametersJson = parameters,
            WarningThreshold = warning, CreatedAt = now, UpdatedAt = now
        };
        var eventLog = Add(CheckType.EventLog, """{"log":"System","level":"error"}""", 1);
        var reboot = Add(CheckType.PendingReboot, "{}", 1);
        var ping = Add(CheckType.Ping, """{"host":"10.0.0.1"}""");
        // A script check follows the language copied from its script; without a known language it is not limited.
        var powerShell = Add(CheckType.Script, $$"""{"script":"{{Guid.NewGuid()}}","language":"PowerShell"}""");
        var bash = Add(CheckType.Script, $$"""{"script":"{{Guid.NewGuid()}}","language":"Bash"}""");
        var numeric = Add(CheckType.Script, $$"""{"script":"{{Guid.NewGuid()}}","language":"1"}""");
        var checks = new[] { eventLog, reboot, ping, powerShell, bash, numeric };
        foreach (var check in checks)
        {
            template.Checks.Add(check);
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.MonitoringTemplates.Add(template);
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate { SiteId = site.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = now });
            await db.Endpoints.Where(e => e.Id == linux.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.OsPlatform, "linux"));
            await db.Endpoints.Where(e => e.Id == mac.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.OsPlatform, "darwin"));
            await db.SaveChangesAsync();
        }

        var expected = new Dictionary<Guid, Guid[]>
        {
            [windows.Id] = [eventLog.Id, reboot.Id, ping.Id, powerShell.Id, numeric.Id],
            [linux.Id] = [reboot.Id, ping.Id, bash.Id, numeric.Id],
            [mac.Id] = [ping.Id, bash.Id, numeric.Id]
        };

        await using var context = _db.DbFactory.CreateSystem();
        foreach (var (endpointId, checkIds) in expected)
        {
            var endpoint = await context.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpointId);
            var resolved = await EffectiveCheckResolver.LoadAsync(context, endpoint, includeDisabledOnEndpoint: false, CancellationToken.None);
            Assert.Equal(checkIds.Order(), resolved.Select(c => c.Id).Order());

            foreach (var definition in checks)
            {
                var applies = await context.Database.SqlQueryRaw<bool>(
                        AppliesQuerySql,
                        new NpgsqlParameter("endpointId", endpointId), new NpgsqlParameter("checkId", definition.Id))
                    .SingleAsync();
                Assert.Equal(checkIds.Contains(definition.Id), applies);
            }
        }
    }
}
