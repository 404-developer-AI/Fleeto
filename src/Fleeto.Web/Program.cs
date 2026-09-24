using System.Threading.RateLimiting;
using Fleeto.Core.Domain;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web;
using Fleeto.Web.Account;
using Fleeto.Web.Api;
using Fleeto.Web.Components;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Logging: console for containers, plus a rolling file (logs/fleeto-web-<date>.log). Never log secrets.
builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss ");
builder.Logging.AddFile(options =>
{
    options.LogDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs");
    options.FileName = "fleeto-web-";
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

// Antiforgery tokens and authentication cookies are protected with these keys, so they must survive restarts. The directory is
// a volume only the web container mounts, and every key in it is sealed with the root key (0.6.0).
var keysDirectory = Environment.ExpandEnvironmentVariables(builder.Configuration["DataProtection:KeysDirectory"] ?? "/app/keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
    .SetApplicationName("Fleeto.Web")
    .ProtectKeysWithRootKey();

builder.Services.AddFleetoInfrastructure(builder.Configuration, FleetoComponent.Web);

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(WebServiceRegistration.ConfigureIdentity)
    .AddFleetoIdentityStores();

// A changed security stamp (password, roles, two-factor reset, deletion) ends other sessions within five minutes.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromMinutes(5);
    // The refreshed principal is built from the user store, which does not know how this session signed in (0.5.0).
    options.OnRefreshingPrincipal = context =>
    {
        TwoFactorGate.CarryOverSecondFactor(context.CurrentPrincipal, context.NewPrincipal);
        return Task.CompletedTask;
    };
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/api/account/logout";
    options.AccessDeniedPath = "/access-denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    // __Host- prefix: Secure, path /, no domain attribute, so no other host can set or read it.
    options.Cookie.Name = "__Host-fleeto-auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    // Lax rather than Strict: links in alert emails must open the page for a signed-in user. Lax still withholds the
    // cookie from cross-site posts, frames and WebSocket handshakes; every form post also carries an antiforgery token.
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
{
    options.Cookie.Name = "__Host-fleeto-2fa";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-fleeto-af";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddAuthorizationBuilder().AddFleetoPolicies();

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
builder.Services.AddFleetoPublicApi(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddFleetoWebServices();

var app = builder.Build();

// Fail at start, with a clear message, when the root key or the database password is missing, rather than on the first page.
try
{
    app.Services.GetRequiredService<ISecretProtector>();
    app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>();
    KeyRingProtection.RetireUnencryptedKeys(app.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>(),
        new DirectoryInfo(keysDirectory), app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "fleeto-web cannot start: {Message}", ex.Message);
    throw;
}

app.Logger.LogInformation("fleeto-web {Version} starting", FleetoVersion.Current);

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

app.UseFleetoSecurityHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseFleetoPublicApiProblems();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TwoFactorEnforcementMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapEntraSignInEndpoints();
app.MapOperationalEndpoints();
app.MapFleetoPublicApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
