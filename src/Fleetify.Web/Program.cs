using System.Threading.RateLimiting;
using Fleetify.Core.Domain;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure;
using Fleetify.Infrastructure.Hosting;
using Fleetify.Infrastructure.Identity;
using Fleetify.Web;
using Fleetify.Web.Account;
using Fleetify.Web.Api;
using Fleetify.Web.Components;
using Fleetify.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Logging: console for containers, plus a rolling file (logs/fleetify-web-<date>.log). Never log secrets.
builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss ");
builder.Logging.AddFile(options =>
{
    options.LogDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs");
    options.FileName = "fleetify-web-";
    options.Extension = "log";
    options.FileSizeLimit = 10 * 1024 * 1024;
    options.RetainedFileCountLimit = 14;
});

// Container: plain HTTP on Web:HttpPort behind Caddy. Development: the URLs from launchSettings (https://localhost:7100).
var httpPort = builder.Configuration.GetValue("Web:HttpPort", 0);
if (httpPort > 0)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(httpPort);
        options.AddServerHeader = false;
    });
}
else
{
    builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
}

// Antiforgery tokens and authentication cookies are protected with these keys, so they must survive restarts.
// Known gap (revisit): the key ring is stored unencrypted in its directory; protecting it with a certificate or the root key
// is planned. The directory is a volume only the web container mounts.
var keysDirectory = Environment.ExpandEnvironmentVariables(builder.Configuration["DataProtection:KeysDirectory"] ?? "/app/keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("Fleetify.Web");

builder.Services.AddFleetifyInfrastructure(builder.Configuration, FleetifyComponent.Web);

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(WebServiceRegistration.ConfigureIdentity)
    .AddFleetifyIdentityStores();

// A changed security stamp (password, roles, two-factor reset, deletion) ends other sessions within five minutes.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(5));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/api/account/logout";
    options.AccessDeniedPath = "/access-denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    // __Host- prefix: Secure, path /, no domain attribute, so no other host can set or read it.
    options.Cookie.Name = "__Host-fleetify-auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    // Lax rather than Strict: links in alert emails must open the page for a signed-in user. Lax still withholds the
    // cookie from cross-site posts, frames and WebSocket handshakes; every form post also carries an antiforgery token.
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
{
    options.Cookie.Name = "__Host-fleetify-2fa";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-fleetify-af";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddAuthorizationBuilder().AddFleetifyPolicies();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("Too many attempts from this address. Wait a few minutes, then try again.", cancellationToken);
    };

    // Credential stuffing and password spraying: lockout protects one account, this protects the endpoint.
    options.AddPolicy(AccountEndpoints.AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        EndpointExtensions.ClientPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));

    // The agent download is anonymous and large; a generous limit that still stops someone from draining bandwidth.
    options.AddPolicy(AccountEndpoints.DownloadRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        EndpointExtensions.ClientPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
});

// After AddRateLimiter: API rejections are answered as problem details, everything else as above.
builder.Services.AddFleetifyPublicApi(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddFleetifyWebServices();

var app = builder.Build();

// Fail at start, with a clear message, when the root key or the database password is missing, rather than on the first page.
try
{
    app.Services.GetRequiredService<ISecretProtector>();
    app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "fleetify-web cannot start: {Message}", ex.Message);
    throw;
}

app.Logger.LogInformation("fleetify-web {Version} starting", FleetifyVersion.Current);

if (app.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        // Exactly one proxy (Caddy) sits in front of the web container.
        ForwardLimit = 1
    };
    // Caddy reaches the container over the instance's Docker network, not loopback: trust exactly that network.
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var network in app.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
    {
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }

    app.UseForwardedHeaders(forwarded);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (!app.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
{
    app.UseHttpsRedirection();
}

app.UseFleetifySecurityHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseFleetifyPublicApiProblems();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TwoFactorEnforcementMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapOperationalEndpoints();
app.MapFleetifyPublicApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
