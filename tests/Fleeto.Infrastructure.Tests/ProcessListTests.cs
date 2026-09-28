using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The process list of the CPU and memory usage checks (0.6.0): the rules that decide from which value a result carries one and
/// what the alert detail says, and the "process_list_at" parameter the signed configuration gives the agent.
/// </summary>
public sealed class ProcessListRuleTests
{
    [Theory]
    [InlineData(85d, 95d, 85d)]
    [InlineData(null, 95d, 95d)]
    [InlineData(85d, null, 85d)]
    [InlineData(null, null, null)]
    [InlineData(double.NaN, 95d, 95d)]
    public void A_list_starts_at_the_lowest_threshold(double? warning, double? critical, double? expected) =>
        Assert.Equal(expected, ProcessListRules.ListAt(warning, critical));

    [Fact]
    public void Only_CPU_and_memory_usage_checks_carry_a_list()
    {
        Assert.True(ProcessListRules.Applies(CheckType.CpuUsage));
        Assert.True(ProcessListRules.Applies(CheckType.MemoryUsage));
        Assert.False(ProcessListRules.Applies(CheckType.DiskFree));
        Assert.False(ProcessListRules.Applies(CheckType.ProcessRunning));
    }

    [Fact]
    public void A_list_survives_the_round_trip_and_damage_reads_as_none()
    {
        ProcessEntry[] processes = [new(4321, "sqlservr.exe", @"NT SERVICE\MSSQLSERVER", 71.2, 2_147_483_648), new(88, "bash", "root", null, 1024)];

        var json = ProcessListRules.Serialize(processes);

        Assert.Equal(processes, ProcessListRules.Parse(json));
        Assert.Null(ProcessListRules.Serialize([]));
        Assert.Empty(ProcessListRules.Parse(null));
        Assert.Empty(ProcessListRules.Parse("{not json"));
    }

    [Fact]
    public void The_alert_detail_names_the_top_three_processes_without_their_users()
    {
        ProcessEntry[] processes =
        [
            new(1, "sqlservr.exe", @"NT SERVICE\MSSQLSERVER", 71.2, 2_147_483_648),
            new(2, "MsMpEng.exe", @"NT AUTHORITY\SYSTEM", 12, 300 * 1024 * 1024),
            new(3, "svchost.exe", @"NT AUTHORITY\SYSTEM", 3.1, 50 * 1024 * 1024),
            new(4, "explorer.exe", @"CONTOSO\jan", 1, 80 * 1024 * 1024)
        ];

        var cpu = ProcessListRules.AlertDetail(CheckType.CpuUsage, "92.3 % average over 60 s", processes);
        var memory = ProcessListRules.AlertDetail(CheckType.MemoryUsage, "30 GB in use of 32 GB", processes);

        Assert.Equal("92.3 % average over 60 s. Top processes: sqlservr.exe 71.2%, MsMpEng.exe 12%, svchost.exe 3.1%.", cpu);
        Assert.Equal("30 GB in use of 32 GB. Top processes: sqlservr.exe 2 GB, MsMpEng.exe 300 MB, svchost.exe 50 MB.", memory);
        Assert.DoesNotContain("SYSTEM", cpu + memory);
        Assert.DoesNotContain("explorer", cpu);
        Assert.Equal("12.3 GB free", ProcessListRules.AlertDetail(CheckType.DiskFree, "12.3 GB free", processes));
        Assert.Equal("92 %", ProcessListRules.AlertDetail(CheckType.CpuUsage, "92 %", []));
    }
}

[Collection(DatabaseCollection.Name)]
public sealed class ProcessListConfigTests
{
    private readonly TestDatabase _db;

    public ProcessListConfigTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task CPU_and_memory_checks_get_their_lowest_effective_threshold_and_other_checks_nothing()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-PROCESSES");
        await _db.LoadTestLicenseAsync(100_000);
        var now = DateTime.UtcNow;
        CheckDefinition Check(CheckType type, double? warning, double? critical, string parameters = "{}") => new()
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = type.ToString(), Type = type, IntervalSeconds = 300,
            WarningThreshold = warning, CriticalThreshold = critical, ParametersJson = parameters, CreatedAt = now, UpdatedAt = now
        };
        var cpu = Check(CheckType.CpuUsage, 85, 95, """{"process_list_at":"1"}""");
        // A template check whose thresholds the endpoint overrides.
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Memory " + Guid.NewGuid().ToString("N")[..8], CreatedAt = now, UpdatedAt = now };
        var memory = Check(CheckType.MemoryUsage, null, 97);
        memory.EndpointId = null;
        memory.MonitoringTemplateId = template.Id;
        var cpuWithoutThresholds = Check(CheckType.CpuUsage, null, null);
        var disk = Check(CheckType.DiskFree, 15, 5, """{"drive":"C:"}""");
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.MonitoringTemplates.Add(template);
            db.EndpointMonitoringTemplates.Add(new EndpointMonitoringTemplate { EndpointId = endpoint.Id, ClientId = client.Id, MonitoringTemplateId = template.Id, CreatedAt = now });
            db.CheckDefinitions.AddRange(cpu, memory, cpuWithoutThresholds, disk);
            db.EndpointCheckOverrides.Add(new EndpointCheckOverride
            {
                EndpointId = endpoint.Id, CheckDefinitionId = memory.Id, ClientId = client.Id, OverrideThresholds = true,
                WarningThreshold = 90, CriticalThreshold = 97, CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var checks = (await BuildAsync(endpoint.Id)).Checks.ToDictionary(c => c.Id);

        // A value set in the definition never reaches the agent: only the thresholds decide.
        Assert.Equal("85", checks[cpu.Id.ToString("D")].Parameters[ProcessListRules.Parameter]);
        Assert.Equal("90", checks[memory.Id.ToString("D")].Parameters[ProcessListRules.Parameter]);
        Assert.False(checks[cpuWithoutThresholds.Id.ToString("D")].Parameters.ContainsKey(ProcessListRules.Parameter));
        Assert.False(checks[disk.Id.ToString("D")].Parameters.ContainsKey(ProcessListRules.Parameter));
    }

    private async Task<Protocol.Agent.V1.AgentConfig> BuildAsync(Guid endpointId)
    {
        await using var context = _db.DbFactory.CreateSystem();
        return (await new AgentConfigBuilder(_db.Licenses).BuildAsync(context, endpointId, _db.InstanceId))!.Config;
    }
}
