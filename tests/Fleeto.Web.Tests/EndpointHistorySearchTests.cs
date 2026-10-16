using Fleeto.Core.Entities;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees the search of the History tab of an endpoint: alerts, connections and audit entries are searched in the database, over
/// everything the endpoint has (not only the newest that are shown), case-insensitively, with LIKE wildcards taken literally, and never
/// beyond the endpoint.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class EndpointHistorySearchTests
{
    private readonly WebFixture _fixture;

    public EndpointHistorySearchTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private EndpointService Endpoints => _fixture.Services.GetRequiredService<EndpointService>();
    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task The_history_is_searched_in_the_database_over_everything_of_the_endpoint_only()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-HISTORY");
        var other = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-OTHER");

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            foreach (var target in new[] { endpoint, other })
            {
                // The matching rows are the oldest, so a search must look past the newest that a limit would return.
                db.Alerts.Add(Alert(target, "C:", "Less than 10% free disk space on C:", Now.AddDays(-30)));
                db.EndpointEvents.Add(new EndpointEvent { ClientId = client.Id, EndpointId = target.Id, Kind = EndpointEventKind.DuplicateIdentity,
                    Detail = "Seen from 10.0.0.7", Time = Now.AddDays(-30) });
                db.AuditEntries.Add(Audit(target, "endpoint.tier", """{"tier":"Managed","reason":"Contract 100% signed"}""", Now.AddDays(-30)));
                for (var i = 0; i < 5; i++)
                {
                    db.Alerts.Add(Alert(target, $"T{i}", $"CPU usage above 90% ({i})", Now.AddMinutes(-i)));
                    db.EndpointEvents.Add(new EndpointEvent { ClientId = client.Id, EndpointId = target.Id, Kind = EndpointEventKind.Connected,
                        Detail = "From 192.168.1.20", Time = Now.AddMinutes(-i) });
                    db.AuditEntries.Add(Audit(target, "note.create", "{}", Now.AddMinutes(-i)));
                }
            }

            await db.SaveChangesAsync();
        }

        var caller = WebFixture.Technician();

        Assert.Equal(3, (await Endpoints.GetAlertsAsync(caller, endpoint.Id, 3)).Count);
        var disk = Assert.Single(await Endpoints.GetAlertsAsync(caller, endpoint.Id, 3, "FREE DISK"));
        Assert.Equal(endpoint.Id, disk.EndpointId);
        Assert.Equal(5, (await Endpoints.GetAlertsAsync(caller, endpoint.Id, 50, "90%")).Count);
        Assert.Empty(await Endpoints.GetAlertsAsync(caller, endpoint.Id, 50, "9_%"));

        // Events match their detail and the label of their kind.
        Assert.Single(await Endpoints.GetEventsAsync(caller, endpoint.Id, 3, "10.0.0.7"));
        Assert.Single(await Endpoints.GetEventsAsync(caller, endpoint.Id, 3, "duplicate agent"));
        Assert.Equal(5, (await Endpoints.GetEventsAsync(caller, endpoint.Id, 50, "connected")).Count);

        // Audit entries match the action and the details.
        var tier = Assert.Single(await Endpoints.GetAuditAsync(caller, endpoint.Id, 3, "100% signed"));
        Assert.Equal(endpoint.Id.ToString(), tier.TargetId);
        Assert.Single(await Endpoints.GetAuditAsync(caller, endpoint.Id, 3, "ENDPOINT.TIER"));
        Assert.Equal(5, (await Endpoints.GetAuditAsync(caller, endpoint.Id, 50, "note.")).Count);

        // A blank search is no search.
        Assert.Equal(6, (await Endpoints.GetAuditAsync(caller, endpoint.Id, 50, "   ")).Count);
    }

    private static Alert Alert(Endpoint endpoint, string target, string title, DateTime openedAt) => new()
    {
        Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = AlertKind.Check, Target = target,
        Severity = AlertSeverity.Warning, State = AlertState.Resolved, Title = title, OpenedAt = openedAt, UpdatedAt = openedAt,
        ResolvedAt = openedAt.AddMinutes(1), ResolvedReason = "Recovered"
    };

    private static AuditEntry Audit(Endpoint endpoint, string action, string details, DateTime time) => new()
    {
        Time = time, ClientId = endpoint.ClientId, ActorType = AuditActorType.User, ActorId = Guid.NewGuid().ToString(), ActorName = "tech@example.test",
        Action = action, TargetType = "Endpoint", TargetId = endpoint.Id.ToString(), DetailsJson = details
    };
}
