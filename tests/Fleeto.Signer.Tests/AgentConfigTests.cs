using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Microsoft.EntityFrameworkCore;
using CheckType = Fleeto.Core.Entities.CheckType;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees that the signer computes configurations from the database, never signs checks for an agent-only or
/// unlicensed endpoint, issues a new version only when the content changed, and signs what it stores.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AgentConfigTests
{
    private readonly SignerFixture _fixture;

    public AgentConfigTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Agent_only_endpoint_gets_no_checks_even_when_monitoring_templates_are_linked()
    {
        await _fixture.Database.LoadTestLicenseAsync(100);
        var (endpoint, _) = await CreateEndpointWithTemplateAsync(EndpointTier.AgentOnly);

        var (config, _) = await SignConfigAsync(endpoint);

        Assert.Equal(Tier.AgentOnly, config.Tier);
        Assert.Empty(config.Checks);
    }

    [Fact]
    public async Task Managed_endpoint_with_a_license_past_its_grace_period_gets_an_agent_only_configuration()
    {
        await _fixture.Database.LoadTestLicenseAsync(100, expiresAt: _fixture.Now.AddDays(-30));
        try
        {
            var (endpoint, _) = await CreateEndpointWithTemplateAsync(EndpointTier.Managed);

            var (config, _) = await SignConfigAsync(endpoint);

            Assert.Equal(Tier.AgentOnly, config.Tier);
            Assert.Empty(config.Checks);
        }
        finally
        {
            await _fixture.Database.LoadTestLicenseAsync(100);
        }
    }

    [Fact]
    public async Task Unchanged_configuration_does_not_create_a_new_version()
    {
        await _fixture.Database.LoadTestLicenseAsync(100);
        var (endpoint, _) = await CreateEndpointWithTemplateAsync(EndpointTier.Managed);

        var (first, firstRow) = await SignConfigAsync(endpoint);
        var (second, secondRow) = await SignConfigAsync(endpoint);

        Assert.Equal(Tier.Managed, first.Tier);
        Assert.Single(first.Checks);
        Assert.Equal(1, firstRow.Version);
        Assert.Equal(1, secondRow.Version);
        Assert.Equal(firstRow.Payload, secondRow.Payload);
        Assert.Equal(firstRow.Signature, secondRow.Signature);
        Assert.Equal(first, second);
        Assert.Single(_fixture.Database.Bus.PayloadsFor(NotificationChannels.EndpointConfig), p => p == endpoint.Id.ToString());
    }

    [Fact]
    public async Task Changed_monitoring_template_creates_the_next_version_and_publishes_endpoint_config()
    {
        await _fixture.Database.LoadTestLicenseAsync(100);
        var (endpoint, template) = await CreateEndpointWithTemplateAsync(EndpointTier.Managed);
        var (first, _) = await SignConfigAsync(endpoint);
        Assert.Single(first.Checks);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.Add(Check(template, "Spooler", CheckType.ServiceRunning, """{"service":"Spooler"}"""));
            await db.SaveChangesAsync();
        }

        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(1));
        var (second, row) = await SignConfigAsync(endpoint);

        Assert.Equal(2UL, second.Version);
        Assert.Equal(2, row.Version);
        Assert.Equal(2, second.Checks.Count);
        Assert.Contains(second.Checks, c => c.Type == Protocol.Agent.V1.CheckType.ServiceRunning && c.Parameters["service"] == "Spooler");
        Assert.True(second.IssuedAt.ToDateTime() > first.IssuedAt.ToDateTime());
        Assert.Equal(2, _fixture.Database.Bus.PayloadsFor(NotificationChannels.EndpointConfig).Count(p => p == endpoint.Id.ToString()));

        await using var verify = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(2, await verify.Endpoints.Where(e => e.Id == endpoint.Id).Select(e => e.ConfigVersion).SingleAsync());
    }

    [Fact]
    public async Task Configuration_request_for_a_deleted_endpoint_completes_without_a_result()
    {
        var client = await _fixture.Database.CreateClientAsync();

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentConfig, client.Id, Guid.NewGuid(), [], "workers");

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Null(request.Result);
    }

    [Fact]
    public async Task Configuration_request_naming_another_client_is_refused()
    {
        await _fixture.Database.LoadTestLicenseAsync(100);
        var (endpoint, _) = await CreateEndpointWithTemplateAsync(EndpointTier.Managed);
        var otherClient = await _fixture.Database.CreateClientAsync();

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentConfig, otherClient.Id, endpoint.Id, [], "workers");

        Assert.Equal(SigningRequestState.Refused, request.State);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.EndpointConfigs.AnyAsync(c => c.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task Endpoint_adjustments_are_part_of_the_signed_configuration()
    {
        await _fixture.Database.LoadTestLicenseAsync(100);
        var (endpoint, template) = await CreateEndpointWithTemplateAsync(EndpointTier.Managed);
        var disabled = Check(template, "Memory usage", CheckType.MemoryUsage, "{}");
        var extraTemplate = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Extra " + Guid.NewGuid(), CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now };
        var linked = Check(extraTemplate, "Uptime", CheckType.Uptime, "{}");
        var own = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Name = "Spooler", Type = CheckType.ServiceRunning,
            IntervalSeconds = 120, ParametersJson = """{"service":"Spooler"}""", CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
        };
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var cpu = await db.CheckDefinitions.SingleAsync(c => c.MonitoringTemplateId == template.Id);
            db.CheckDefinitions.Add(disabled);
            db.MonitoringTemplates.Add(extraTemplate);
            db.CheckDefinitions.Add(linked);
            db.CheckDefinitions.Add(own);
            db.EndpointMonitoringTemplates.Add(new EndpointMonitoringTemplate
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, MonitoringTemplateId = extraTemplate.Id, CreatedAt = _fixture.Now
            });
            db.EndpointCheckOverrides.Add(new EndpointCheckOverride
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, CheckDefinitionId = cpu.Id, IntervalSeconds = 30, CreatedAt = _fixture.Now,
                UpdatedAt = _fixture.Now
            });
            db.EndpointCheckOverrides.Add(new EndpointCheckOverride
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, CheckDefinitionId = disabled.Id, Disabled = true, CreatedAt = _fixture.Now,
                UpdatedAt = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        var (config, _) = await SignConfigAsync(endpoint);

        Assert.Equal(3, config.Checks.Count);
        Assert.Equal(30u, config.Checks.Single(c => c.Type == Protocol.Agent.V1.CheckType.CpuUsage).IntervalSeconds);
        Assert.DoesNotContain(config.Checks, c => c.Id == disabled.Id.ToString("D"));
        Assert.Contains(config.Checks, c => c.Id == linked.Id.ToString("D"));
        Assert.Equal("Spooler", config.Checks.Single(c => c.Id == own.Id.ToString("D")).Parameters["service"]);
    }

    [Fact]
    public async Task Agent_only_endpoint_gets_no_endpoint_only_checks()
    {
        await _fixture.Database.LoadTestLicenseAsync(100);
        var (endpoint, _) = await CreateEndpointWithTemplateAsync(EndpointTier.AgentOnly);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.Add(new CheckDefinition
            {
                Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Name = "Uptime", Type = CheckType.Uptime,
                WarningThreshold = 30, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        var (config, _) = await SignConfigAsync(endpoint);

        Assert.Equal(Tier.AgentOnly, config.Tier);
        Assert.Empty(config.Checks);
    }

    private async Task<(AgentConfig Config, EndpointConfig Row)> SignConfigAsync(Endpoint endpoint)
    {
        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentConfig, endpoint.ClientId, endpoint.Id, [], "workers");
        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Null(request.Result);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var row = await db.EndpointConfigs.AsNoTracking().SingleAsync(c => c.EndpointId == endpoint.Id);
        var publicKey = await db.InstanceSigningKeys.AsNoTracking().Where(k => k.Id == row.KeyId).Select(k => k.PublicKey).SingleAsync();
        Assert.True(Ed25519.Verify(publicKey, SignatureContexts.AgentConfig, row.Payload, row.Signature));

        var config = AgentConfig.Parser.ParseFrom(row.Payload);
        Assert.Equal(endpoint.Id.ToString("D"), config.EndpointId);
        Assert.Equal(_fixture.Database.InstanceId.ToString("D"), config.InstanceId);
        Assert.Equal((ulong)row.Version, config.Version);
        return (config, row);
    }

    private async Task<(Endpoint Endpoint, MonitoringTemplate Template)> CreateEndpointWithTemplateAsync(EndpointTier tier)
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier);
        var now = _fixture.Now;

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var template = new MonitoringTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = "Signer test template",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MonitoringTemplates.Add(template);
        db.CheckDefinitions.Add(Check(template, "CPU usage", CheckType.CpuUsage, "{}"));
        db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate
        {
            SiteId = site.Id,
            ClientId = client.Id,
            MonitoringTemplateId = template.Id,
            Source = LinkSource.Manual,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        return (endpoint, template);
    }

    private CheckDefinition Check(MonitoringTemplate template, string name, CheckType type, string parameters) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = template.ClientId,
        MonitoringTemplateId = template.Id,
        Name = name,
        Type = type,
        IntervalSeconds = 300,
        ParametersJson = parameters,
        WarningThreshold = 80,
        CriticalThreshold = 95,
        CreatedAt = _fixture.Now,
        UpdatedAt = _fixture.Now
    };
}
