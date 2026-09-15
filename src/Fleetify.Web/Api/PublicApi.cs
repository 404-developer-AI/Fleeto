using System.Threading.RateLimiting;
using Fleetify.Core.Interfaces;
using Fleetify.Web.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Fleetify.Web.Api;

/// <summary>Limits of the public API, section <c>PublicApi</c> in the configuration.</summary>
public sealed class PublicApiOptions
{
    /// <summary>Requests one API key may make per minute (token bucket: bursts up to this number, then one request per 60/N seconds).</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>Requests per minute from one address (IPv6: one /64), before the key is checked. Stops guessing keys.</summary>
    public int AddressRequestsPerMinute { get; set; } = 300;
}

/// <summary>Per-key rate limit, applied after the key is authenticated, so nobody can use up the budget of a key they do not hold.</summary>
public sealed class ApiKeyRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public ApiKeyRateLimiter(IOptions<PublicApiOptions> options)
    {
        var perMinute = Math.Max(1, options.Value.RequestsPerMinute);
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(keyId => RateLimitPartition.GetTokenBucketLimiter(keyId, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = perMinute,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    }

    public RateLimitLease Acquire(Guid keyId) => _limiter.AttemptAcquire(keyId);

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// The public REST API (0.2.1): read-only, authenticated with API keys, rate limited and audited per call. The contract is
/// MD-Files/API.md; the OpenAPI document at <see cref="OpenApiPath"/> is generated from these endpoints, and a test keeps both in step.
/// </summary>
public static class PublicApi
{
    public const string BasePath = "/api/v1";
    public const string OpenApiPath = "/api/v1/openapi.json";
    public const string DocumentName = "v1";
    public const string AddressRateLimitPolicy = "fleetify.api.address";

    private const string CallerItemKey = "fleetify-api-caller";

    public static bool IsApiRequest(HttpContext context) => context.Request.Path.StartsWithSegments(BasePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The API key the current request runs as. Only set inside the API endpoint filter.</summary>
    public static Caller ApiCaller(this HttpContext context) =>
        context.Items[CallerItemKey] as Caller ?? throw new InvalidOperationException("The public API caller is only available inside the API endpoints.");

    /// <summary>Services of the public API. Call after <c>AddRateLimiter</c> so API rejections are answered as problem details.</summary>
    public static IServiceCollection AddFleetifyPublicApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PublicApiOptions>(configuration.GetSection("PublicApi"));
        services.AddSingleton<ApiKeyAuthenticator>();
        services.AddSingleton<ApiKeyRateLimiter>();
        services.AddSingleton<PublicApiQueries>();

        // Numbers are numbers: without this the web defaults also read numbers from strings, and the OpenAPI document then describes every
        // number as "integer or string".
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict);

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AddressRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                EndpointExtensions.ClientPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, context.RequestServices.GetRequiredService<IOptions<PublicApiOptions>>().Value.AddressRequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            var previous = options.OnRejected;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (!IsApiRequest(context.HttpContext))
                {
                    if (previous is not null)
                    {
                        await previous(context, cancellationToken);
                    }

                    return;
                }

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await ApiProblems.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, ApiProblems.RateLimited,
                    "Too many requests from this address.", "Wait a minute, then try again. Spread requests over time instead of sending them at once.");
            };
        });

        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Fleeto API",
                    Version = "v1",
                    Description = "Read-only public API of a Fleeto instance. Authenticate with an API key created in Settings, API keys: " +
                                  "Authorization: Bearer flt_<id>_<secret>. Conventions, errors and examples are in the Fleeto API documentation (API.md)."
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "flt_<id>_<secret>",
                    Description = "An API key created in Settings, API keys."
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("apiKey", document)] = [] }];
                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// Errors under <see cref="BasePath"/> are problem details, never the HTML error pages of the UI: status code pages are switched off for
    /// API requests, and an error response without a body (no matching route, wrong method, a parameter of the wrong type) gets one. Place
    /// directly after <c>UseStatusCodePagesWithReExecute</c>.
    /// </summary>
    public static IApplicationBuilder UseFleetifyPublicApiProblems(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsApiRequest(context))
            {
                await next(context);
                return;
            }

            if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
            {
                statusCodePages.Enabled = false;
            }

            // Responses carry personal data (user names, addresses, notes): never kept by a browser or proxy cache.
            context.Response.Headers.CacheControl = "no-store";
            await next(context);

            var response = context.Response;
            if (response.HasStarted || response.StatusCode < 400 || response.ContentLength is not null || !string.IsNullOrEmpty(response.ContentType))
            {
                return;
            }

            var (code, title, detail) = response.StatusCode switch
            {
                StatusCodes.Status404NotFound => (ApiProblems.NotFound, "No API endpoint matches this path.",
                    "Check the path against the API documentation. Every path starts with /api/v1/."),
                StatusCodes.Status405MethodNotAllowed => (ApiProblems.MethodNotAllowed, "The public API only answers GET requests.",
                    "Use GET. The API is read-only."),
                StatusCodes.Status400BadRequest => (ApiProblems.InvalidParameter, "A parameter has a value of the wrong type.",
                    "Check the parameters: ids are UUIDs, limit is a whole number and online is true or false."),
                _ => (ApiProblems.InternalError, "The request could not be completed.", "Try again. If it keeps happening, contact the administrator of this instance.")
            };
            await ApiProblems.WriteAsync(context, response.StatusCode, code, title, detail);
        });

    public static IEndpointRouteBuilder MapFleetifyPublicApi(this IEndpointRouteBuilder app)
    {
        // The document describes the API, not any data; it is public so tools can import it before a key exists.
        app.MapOpenApi(OpenApiPath).AllowAnonymous().RequireRateLimiting(AddressRateLimitPolicy);

        var group = app.MapGroup(BasePath)
            .AllowAnonymous()
            .RequireRateLimiting(AddressRateLimitPolicy)
            .AddEndpointFilter(AuthenticateAndAuditAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        PublicApiEndpoints.Map(group);
        return app;
    }

    /// <summary>
    /// Every API call: check the key, apply the key's rate limit, run the endpoint as the key, and write an audit entry before the response
    /// leaves. When the audit entry cannot be written the data is not sent.
    /// </summary>
    private static async ValueTask<object?> AuthenticateAndAuditAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Fleetify.Web.Api");
        var cancellationToken = http.RequestAborted;

        ApiAuthentication authentication;
        try
        {
            authentication = await services.GetRequiredService<ApiKeyAuthenticator>()
                .AuthenticateAsync(http.Request.Headers.Authorization.ToString(), http.RemoteIp(), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Without the database no key can be checked: answer as the API does, not with the HTML error page of the UI.
            logger.LogError(ex, "A public API key could not be checked");
            return InternalError();
        }

        if (authentication.Caller is not { } caller)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return ApiProblems.Problem(StatusCodes.Status401Unauthorized, ApiProblems.Unauthorized, authentication.Problem!,
                "Send a valid key as \"Authorization: Bearer flt_<id>_<secret>\". An admin creates keys in Settings, API keys.");
        }

        using (var lease = services.GetRequiredService<ApiKeyRateLimiter>().Acquire(caller.UserId))
        {
            if (!lease.IsAcquired)
            {
                var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? (int)Math.Ceiling(retryAfter.TotalSeconds) : 60;
                http.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ApiProblems.Problem(StatusCodes.Status429TooManyRequests, ApiProblems.RateLimited, "This API key made too many requests.",
                    $"Wait {Math.Max(1, seconds)} seconds (the Retry-After header), then try again. Page through lists instead of fetching every item.");
            }
        }

        http.Items[CallerItemKey] = caller;
        object? result;
        try
        {
            result = await next(context);
        }
        catch (InvalidCursorException)
        {
            result = ApiProblems.InvalidParameterResult("cursor", "The cursor is not valid.",
                "Pass the nextCursor value of the previous page unchanged, with the same filters, or leave cursor out to start at the first page.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Public API call {Method} {Path} by API key {ApiKeyId} failed", http.Request.Method, http.Request.Path, caller.UserId);
            result = InternalError();
        }

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? string.Empty;
        try
        {
            await services.GetRequiredService<IAuditLog>().WriteAsync(caller.Audit(AuditActions.ApiRequest, "Api", route, null, new
            {
                http.Request.Method,
                Path = Truncate(http.Request.Path.Value, 300),
                Query = Truncate(http.Request.QueryString.Value, 500),
                Status = status
            }), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The audit entry of a public API call by API key {ApiKeyId} could not be written; the response was withheld", caller.UserId);
            return InternalError();
        }

        return result;
    }

    private static IResult InternalError() =>
        ApiProblems.Problem(StatusCodes.Status500InternalServerError, ApiProblems.InternalError, "The request could not be completed.",
            "Try again. If it keeps happening, contact the administrator of this instance.");

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
