using System.Net;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// What Fleeto sends to Action1 to deploy updates, and what it makes of the answer (0.4.0 step 3). Action1 publishes no
/// downloadable contract, so the shape Fleeto depends on is pinned here: the endpoints and packages of a deployment, the
/// restart options, and the rule that a status Fleeto does not know stays unknown instead of being read as success.
/// </summary>
public class Action1DeploymentTests
{
    private static readonly Action1Credentials Credentials = new("api-key-deploy@action1.com", "n0tinamessage-1a2b3c");

    private static Action1Deployment Deployment(IReadOnlyList<Action1Package> packages, bool autoReboot = false) =>
        new("Fleeto 2026-09-21 10:00 UTC", "2 updates on 1 endpoint", ["endpoint-1", "endpoint-2"], packages, autoReboot,
            "Save your work.", 1800, 1440);

    [Fact]
    public void A_deployment_names_every_endpoint_and_every_chosen_package()
    {
        var body = Deployment([new Action1Package("package-a", "1.2.3"), new Action1Package("package-b", "4.5")]).ToJson();

        var json = JsonDocument.Parse(body.ToJsonString()).RootElement;
        Assert.Equal("1440", json.GetProperty("retry_minutes").GetString());
        Assert.Equal(["endpoint-1", "endpoint-2"], json.GetProperty("endpoints").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.All(json.GetProperty("endpoints").EnumerateArray(), e => Assert.Equal("Endpoint", e.GetProperty("type").GetString()));

        var action = json.GetProperty("actions")[0];
        Assert.Equal("deploy_update", action.GetProperty("template_id").GetString());
        var parameters = action.GetProperty("params");
        Assert.Equal("Specified", parameters.GetProperty("scope").GetString());
        Assert.Equal("1.2.3", parameters.GetProperty("packages")[0].GetProperty("package-a").GetString());
        Assert.Equal("4.5", parameters.GetProperty("packages")[1].GetProperty("package-b").GetString());
        // Nothing restarts unless the technician asked for it, and then the endpoint's user is told first.
        Assert.Equal("no", parameters.GetProperty("reboot_options").GetProperty("auto_reboot").GetString());
        Assert.False(parameters.GetProperty("reboot_options").TryGetProperty("message_text", out _));
    }

    [Fact]
    public void A_deployment_of_everything_missing_leaves_the_choice_to_the_product()
    {
        var body = Deployment([], autoReboot: true).ToJson();

        var parameters = JsonDocument.Parse(body.ToJsonString()).RootElement.GetProperty("actions")[0].GetProperty("params");
        Assert.Equal("All", parameters.GetProperty("scope").GetString());
        Assert.Equal("default", parameters.GetProperty("packages")[0].GetProperty("default").GetString());
        var reboot = parameters.GetProperty("reboot_options");
        Assert.Equal("yes", reboot.GetProperty("auto_reboot").GetString());
        Assert.Equal("yes", reboot.GetProperty("show_message").GetString());
        Assert.Equal("Save your work.", reboot.GetProperty("message_text").GetString());
        Assert.Equal(1800, reboot.GetProperty("timeout").GetInt32());
    }

    [Theory]
    [InlineData("Succeeded", PatchDeploymentTargetState.Succeeded)]
    [InlineData("completed", PatchDeploymentTargetState.Succeeded)]
    [InlineData("Failed", PatchDeploymentTargetState.Failed)]
    [InlineData("In progress", PatchDeploymentTargetState.Running)]
    [InlineData("Scheduled", PatchDeploymentTargetState.Pending)]
    [InlineData("Something Action1 invents later", PatchDeploymentTargetState.Unknown)]
    [InlineData("", PatchDeploymentTargetState.Unknown)]
    public void A_status_Fleeto_does_not_know_is_unknown_and_never_success(string status, PatchDeploymentTargetState expected) =>
        Assert.Equal(expected, Action1EndpointResult.ParseState(status));

    [Fact]
    public void An_endpoint_result_keeps_the_word_Action1_used()
    {
        var item = JsonDocument.Parse("""{"endpoint_id":"endpoint-1","status":"Reboot pending"}""").RootElement;

        var result = Action1EndpointResult.From(item);

        Assert.NotNull(result);
        Assert.Equal("endpoint-1", result!.EndpointId);
        Assert.Equal(PatchDeploymentTargetState.Unknown, result.State);
        Assert.Equal("Reboot pending", result.Status);
    }

    [Fact]
    public void A_result_without_an_endpoint_is_dropped_rather_than_matched_to_nothing() =>
        Assert.Null(Action1EndpointResult.From(JsonDocument.Parse("""{"status":"Succeeded"}""").RootElement));

    [Fact]
    public void A_missing_update_takes_its_version_from_the_list_of_versions()
    {
        var item = JsonDocument.Parse("""
            {"id":"package-a","name":"Google Chrome","vendor":"Google","security_severity":"Important",
             "versions":[{"version":"126.0.6478.115"},{"version":"125.0.1"}]}
            """).RootElement;

        var update = Action1MissingUpdate.From(item);

        Assert.NotNull(update);
        Assert.Equal("package-a", update!.Id);
        // Without a version a deployment cannot name the package, so reading it is what makes the update deployable.
        Assert.Equal("126.0.6478.115", update.Version);
    }

    [Fact]
    public async Task A_deployment_Action1_does_not_name_is_not_reported_as_started()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero));
        using var client = new Action1Client(Credentials, Action1Region.Europe, new RequestBudget(60, time), time,
            handler: new NamelessHandler());

        var result = await client.StartDeploymentAsync("org-1", Deployment([]));

        Assert.False(result.Ok);
        Assert.Contains("Action1 console", result.Message);
        Assert.True(result.Permanent);
        Assert.DoesNotContain(Credentials.ClientSecret, result.Message);
    }

    [Fact]
    public async Task A_deployment_without_endpoints_is_refused_before_anything_is_sent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero));
        var handler = new NamelessHandler();
        using var client = new Action1Client(Credentials, Action1Region.Europe, new RequestBudget(60, time), time, handler: handler);

        var result = await client.StartDeploymentAsync("org-1", new Action1Deployment("x", "y", [], [], false, "", 60, 60));

        Assert.False(result.Ok);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>Accepts the deployment but answers without an id, which is what Fleeto needs to follow it.</summary>
    private sealed class NamelessHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600,"token_type":"Bearer"}"""));
            }

            Calls++;
            return Task.FromResult(Json("""{"status":"Created"}"""));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
