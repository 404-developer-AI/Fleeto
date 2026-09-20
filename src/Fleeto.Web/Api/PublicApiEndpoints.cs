using System.ComponentModel;
using Fleeto.Core.Entities;
using Fleeto.Web.Services;

namespace Fleeto.Web.Api;

/// <summary>
/// The routes of the public API. Every route here must be documented in MD-Files/API.md (a test compares the OpenAPI document with it);
/// a feature of Fleeto that has no route yet belongs on MD-Files/API-WAITLIST.md.
/// </summary>
internal static class PublicApiEndpoints
{
    private const string CursorDescription = "The nextCursor of the previous page. Leave out for the first page.";
    private const string LimitDescription = "Items per page, 1 to 200. Default 50.";

    public static void Map(RouteGroupBuilder api)
    {
        // Clients

        api.MapGet("/clients", async (HttpContext http, PublicApiQueries queries,
                [Description("Part of the client code or name, case-insensitive.")] string? search,
                [Description(CursorDescription)] string? cursor, [Description(LimitDescription)] int? limit, CancellationToken cancellationToken) =>
            {
                if (Validate(limit, search) is { } problem)
                {
                    return problem;
                }

                return Results.Ok(await queries.ListClientsAsync(http.ApiCaller(), search, cursor, limit ?? PublicApiQueries.DefaultLimit, cancellationToken));
            })
            .WithName("listClients").WithTags("Clients").WithSummary("List clients, ordered by client code.")
            .Produces<ApiPage<ApiClient>>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/clients/{clientId:guid}", async (HttpContext http, PublicApiQueries queries, Guid clientId, CancellationToken cancellationToken) =>
                await queries.GetClientAsync(http.ApiCaller(), clientId, cancellationToken) is { } client
                    ? Results.Ok(client)
                    : ApiProblems.NotFoundResult("client"))
            .WithName("getClient").WithTags("Clients").WithSummary("Get one client.")
            .Produces<ApiClient>().ProducesProblem(StatusCodes.Status404NotFound);

        // Sites

        api.MapGet("/sites", async (HttpContext http, PublicApiQueries queries,
                [Description("Only the sites of this client.")] Guid? clientId,
                [Description(CursorDescription)] string? cursor, [Description(LimitDescription)] int? limit, CancellationToken cancellationToken) =>
            {
                if (Validate(limit, null) is { } problem)
                {
                    return problem;
                }

                return Results.Ok(await queries.ListSitesAsync(http.ApiCaller(), clientId, cursor, limit ?? PublicApiQueries.DefaultLimit, cancellationToken));
            })
            .WithName("listSites").WithTags("Sites").WithSummary("List sites, ordered by name.")
            .Produces<ApiPage<ApiSite>>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/sites/{siteId:guid}", async (HttpContext http, PublicApiQueries queries, Guid siteId, CancellationToken cancellationToken) =>
                await queries.GetSiteAsync(http.ApiCaller(), siteId, cancellationToken) is { } site
                    ? Results.Ok(site)
                    : ApiProblems.NotFoundResult("site"))
            .WithName("getSite").WithTags("Sites").WithSummary("Get one site.")
            .Produces<ApiSite>().ProducesProblem(StatusCodes.Status404NotFound);

        // Endpoints

        api.MapGet("/endpoints", async (HttpContext http, PublicApiQueries queries,
                [Description("Only the endpoints of this client.")] Guid? clientId,
                [Description("Only the endpoints of this site.")] Guid? siteId,
                [Description("workstation or server (the class that applies, override included).")] string? @class,
                [Description("agent_only or managed (the stored tier).")] string? tier,
                [Description("true: only online endpoints; false: only offline endpoints.")] bool? online,
                [Description("Part of the hostname, case-insensitive.")] string? search,
                [Description(CursorDescription)] string? cursor, [Description(LimitDescription)] int? limit, CancellationToken cancellationToken) =>
            {
                if (Validate(limit, search) is { } problem)
                {
                    return problem;
                }

                if (!TryParse(@class, ClassValues, out var endpointClass))
                {
                    return InvalidValue("class", @class!, ClassValues.Keys);
                }

                if (!TryParse(tier, TierValues, out var endpointTier))
                {
                    return InvalidValue("tier", tier!, TierValues.Keys);
                }

                var filter = new ApiEndpointFilter(clientId, siteId, endpointClass, endpointTier, online, search);
                return Results.Ok(await queries.ListEndpointsAsync(http.ApiCaller(), filter, cursor, limit ?? PublicApiQueries.DefaultLimit, cancellationToken));
            })
            .WithName("listEndpoints").WithTags("Endpoints").WithSummary("List endpoints with their status, ordered by hostname.")
            .Produces<ApiPage<ApiEndpoint>>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/endpoints/{endpointId:guid}", async (HttpContext http, PublicApiQueries queries, Guid endpointId, CancellationToken cancellationToken) =>
                await queries.GetEndpointAsync(http.ApiCaller(), endpointId, cancellationToken) is { } endpoint
                    ? Results.Ok(endpoint)
                    : ApiProblems.NotFoundResult("endpoint"))
            .WithName("getEndpoint").WithTags("Endpoints").WithSummary("Get one endpoint with its status.")
            .Produces<ApiEndpoint>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/endpoints/{endpointId:guid}/inventory", async (HttpContext http, PublicApiQueries queries, Guid endpointId,
                CancellationToken cancellationToken) =>
            {
                var (found, inventory) = await queries.GetInventoryAsync(http.ApiCaller(), endpointId, cancellationToken);
                return !found ? ApiProblems.NotFoundResult("endpoint")
                    : inventory is null ? ApiProblems.Problem(StatusCodes.Status404NotFound, ApiProblems.NotFound,
                        "The endpoint has not reported an inventory yet.", "Try again after the agent has been online for a few minutes.")
                    : Results.Ok(inventory);
            })
            .WithName("getEndpointInventory").WithTags("Endpoints").WithSummary("Get the latest inventory of an endpoint: hardware, disks, network, software, services.")
            .Produces<ApiInventory>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/endpoints/{endpointId:guid}/patches", async (HttpContext http, PublicApiQueries queries, Guid endpointId,
                CancellationToken cancellationToken) =>
            {
                var read = await queries.GetPatchStateAsync(http.ApiCaller(), endpointId, cancellationToken);
                return read is null ? ApiProblems.NotFoundResult("endpoint")
                    : !read.Managed ? ApiProblems.NotManagedResult("Patch management")
                    : read.Value is null ? ApiProblems.Problem(StatusCodes.Status404NotFound, ApiProblems.NotFound,
                        "Patch management does not cover this endpoint.",
                        "Connect Action1 in Settings, Integrations, map the client to an organization, and install the Action1 agent on the endpoint.")
                    : Results.Ok(read.Value);
            })
            .WithName("getEndpointPatches").WithTags("Endpoints")
            .WithSummary("Get the patch state of a managed endpoint: compliance, missing updates and whether patch management still covers it.")
            .Produces<ApiPatchState>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/endpoints/{endpointId:guid}/checks", async (HttpContext http, PublicApiQueries queries, Guid endpointId,
                CancellationToken cancellationToken) =>
            {
                var read = await queries.GetChecksAsync(http.ApiCaller(), endpointId, cancellationToken);
                return read is null ? ApiProblems.NotFoundResult("endpoint")
                    : !read.Managed ? ApiProblems.NotManagedResult("Checks")
                    : Results.Ok(read.Value);
            })
            .WithName("getEndpointChecks").WithTags("Endpoints").WithSummary("Get every check that applies to a managed endpoint, with its current state.")
            .Produces<ApiEndpointChecks>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/endpoints/{endpointId:guid}/notes", async (HttpContext http, PublicApiQueries queries, Guid endpointId,
                [Description(CursorDescription)] string? cursor, [Description(LimitDescription)] int? limit, CancellationToken cancellationToken) =>
            {
                if (Validate(limit, null) is { } problem)
                {
                    return problem;
                }

                var read = await queries.ListNotesAsync(http.ApiCaller(), endpointId, cursor, limit ?? PublicApiQueries.DefaultLimit, cancellationToken);
                return read is null ? ApiProblems.NotFoundResult("endpoint")
                    : !read.Managed ? ApiProblems.NotManagedResult("Notes")
                    : Results.Ok(read.Value);
            })
            .WithName("listEndpointNotes").WithTags("Endpoints").WithSummary("List the notes of a managed endpoint, newest first.")
            .Produces<ApiPage<ApiNote>>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Alerts

        api.MapGet("/alerts", async (HttpContext http, PublicApiQueries queries,
                [Description("open, acknowledged, on_hold or resolved. Leave out for every state. open and acknowledged leave out alerts on hold.")] string? state,
                [Description("warning or critical.")] string? severity,
                [Description("Only the alerts of this client.")] Guid? clientId,
                [Description("Only the alerts of this endpoint.")] Guid? endpointId,
                [Description(CursorDescription)] string? cursor, [Description(LimitDescription)] int? limit, CancellationToken cancellationToken) =>
            {
                if (Validate(limit, null) is { } problem)
                {
                    return problem;
                }

                if (!TryParse(state, AlertStateValues, out var alertState))
                {
                    return InvalidValue("state", state!, AlertStateValues.Keys);
                }

                if (!TryParse(severity, SeverityValues, out var alertSeverity))
                {
                    return InvalidValue("severity", severity!, SeverityValues.Keys);
                }

                var filter = new ApiAlertFilter(alertState ?? AlertStateFilter.All, alertSeverity, clientId, endpointId);
                return Results.Ok(await queries.ListAlertsAsync(http.ApiCaller(), filter, cursor, limit ?? PublicApiQueries.DefaultLimit, cancellationToken));
            })
            .WithName("listAlerts").WithTags("Alerts").WithSummary("List alerts, newest first.")
            .Produces<ApiPage<ApiAlert>>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/alerts/{alertId:guid}", async (HttpContext http, PublicApiQueries queries, Guid alertId, CancellationToken cancellationToken) =>
                await queries.GetAlertAsync(http.ApiCaller(), alertId, cancellationToken) is { } alert
                    ? Results.Ok(alert)
                    : ApiProblems.NotFoundResult("alert"))
            .WithName("getAlert").WithTags("Alerts").WithSummary("Get one alert.")
            .Produces<ApiAlert>().ProducesProblem(StatusCodes.Status404NotFound);

        // Jobs

        api.MapGet("/jobs", async (HttpContext http, PublicApiQueries queries,
                [Description("Only the jobs of this client.")] Guid? clientId,
                [Description("Only the jobs of this endpoint.")] Guid? endpointId,
                [Description("pending_signature, queued, running, succeeded, failed, expired, refused, lost or cancelled.")] string? state,
                [Description(CursorDescription)] string? cursor, [Description(LimitDescription)] int? limit, CancellationToken cancellationToken) =>
            {
                if (Validate(limit, null) is { } problem)
                {
                    return problem;
                }

                if (!TryParse(state, JobStateValues, out var jobState))
                {
                    return InvalidValue("state", state!, JobStateValues.Keys);
                }

                var filter = new ApiJobFilter(clientId, endpointId, jobState);
                return Results.Ok(await queries.ListJobsAsync(http.ApiCaller(), filter, cursor, limit ?? PublicApiQueries.DefaultLimit, cancellationToken));
            })
            .WithName("listJobs").WithTags("Jobs").WithSummary("List jobs, newest first.")
            .Produces<ApiPage<ApiJob>>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/jobs/{jobId:guid}", async (HttpContext http, PublicApiQueries queries, Guid jobId, CancellationToken cancellationToken) =>
                await queries.GetJobAsync(http.ApiCaller(), jobId, cancellationToken) is { } job
                    ? Results.Ok(job)
                    : ApiProblems.NotFoundResult("job"))
            .WithName("getJob").WithTags("Jobs").WithSummary("Get one job.")
            .Produces<ApiJob>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/jobs/{jobId:guid}/output", async (HttpContext http, PublicApiQueries queries, Guid jobId, CancellationToken cancellationToken) =>
                await queries.GetJobOutputAsync(http.ApiCaller(), jobId, cancellationToken) is { } output
                    ? Results.Ok(output)
                    : ApiProblems.NotFoundResult("job"))
            .WithName("getJobOutput").WithTags("Jobs").WithSummary("Get the output of a job: the first megabyte of stdout and of stderr.")
            .Produces<ApiJobOutput>().ProducesProblem(StatusCodes.Status404NotFound);
    }

    // Query values. Written out, so the API values stay the same when an internal name changes.

    internal static readonly IReadOnlyDictionary<string, EndpointClass> ClassValues = new Dictionary<string, EndpointClass>
    {
        ["workstation"] = EndpointClass.Workstation,
        ["server"] = EndpointClass.Server
    };

    internal static readonly IReadOnlyDictionary<string, EndpointTier> TierValues = new Dictionary<string, EndpointTier>
    {
        ["agent_only"] = EndpointTier.AgentOnly,
        ["managed"] = EndpointTier.Managed
    };

    internal static readonly IReadOnlyDictionary<string, AlertStateFilter> AlertStateValues = new Dictionary<string, AlertStateFilter>
    {
        ["open"] = AlertStateFilter.Open,
        ["acknowledged"] = AlertStateFilter.Acknowledged,
        ["on_hold"] = AlertStateFilter.OnHold,
        ["resolved"] = AlertStateFilter.Resolved
    };

    internal static readonly IReadOnlyDictionary<string, AlertSeverity> SeverityValues = new Dictionary<string, AlertSeverity>
    {
        ["warning"] = AlertSeverity.Warning,
        ["critical"] = AlertSeverity.Critical
    };

    internal static readonly IReadOnlyDictionary<string, JobState> JobStateValues = new Dictionary<string, JobState>
    {
        ["pending_signature"] = JobState.PendingSignature,
        ["queued"] = JobState.Queued,
        ["running"] = JobState.Running,
        ["succeeded"] = JobState.Succeeded,
        ["failed"] = JobState.Failed,
        ["expired"] = JobState.Expired,
        ["refused"] = JobState.Refused,
        ["lost"] = JobState.Lost,
        ["cancelled"] = JobState.Cancelled
    };

    private static bool TryParse<T>(string? value, IReadOnlyDictionary<string, T> values, out T? parsed) where T : struct
    {
        parsed = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!values.TryGetValue(value, out var found))
        {
            return false;
        }

        parsed = found;
        return true;
    }

    private static IResult InvalidValue(string parameter, string value, IEnumerable<string> allowed) =>
        ApiProblems.InvalidParameterResult(parameter, $"\"{(value.Length <= 50 ? value : value[..50] + "…")}\" is not a valid value for {parameter}.",
            $"Use one of: {string.Join(", ", allowed)}.");

    private static IResult? Validate(int? limit, string? search)
    {
        if (limit is < 1 or > PublicApiQueries.MaxLimit)
        {
            return ApiProblems.InvalidParameterResult("limit", "The limit is out of range.", $"Use a limit from 1 to {PublicApiQueries.MaxLimit}.");
        }

        if (search?.Length > PublicApiQueries.MaxSearchLength)
        {
            return ApiProblems.InvalidParameterResult("search", "The search text is too long.", $"Use at most {PublicApiQueries.MaxSearchLength} characters.");
        }

        return null;
    }
}
