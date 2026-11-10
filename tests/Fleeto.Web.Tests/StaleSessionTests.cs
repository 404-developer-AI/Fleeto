using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Account;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleeto.Web.Tests;

/// <summary>
/// A test server with the authentication of Program (Identity cookies, the security stamp validator of 5 minutes with its
/// OnRefreshingPrincipal, the cookie and antiforgery options) and the real account endpoints (MapAccountEndpoints), plus test-only
/// endpoints: a sign-in that issues the cookie a completed two-factor sign-in gets, a "who am I" behind [Authorize] and one that
/// hands out an antiforgery token.
/// </summary>
public sealed class AccountHostFixture : WebFixtureBase
{
    private WebApplication? _app;

    public AccountHostFixture() : base("web_account_host")
    {
    }

    public HttpClient Client { get; private set; } = null!;

    protected override async Task<IServiceProvider> BuildServicesAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        AddServices(builder.Services);

        // As Program: AddIdentity = cookies + SignInManager + security stamp validator (AddServices already added the core and stores).
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        }).AddIdentityCookies();
        new IdentityBuilder(typeof(ApplicationUser), typeof(ApplicationRole), builder.Services).AddSignInManager();
        builder.Services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = TimeSpan.FromMinutes(5);
            options.OnRefreshingPrincipal = context =>
            {
                TwoFactorGate.CarryOverSecondFactor(context.CurrentPrincipal, context.NewPrincipal);
                return Task.CompletedTask;
            };
        });
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/account/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Cookie.Name = StaleSessionTests.AuthCookie;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = StaleSessionTests.AntiforgeryCookie;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            foreach (var policy in new[] { AccountEndpoints.AuthRateLimitPolicy, AccountEndpoints.DownloadRateLimitPolicy })
            {
                options.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("test"));
            }
        });

        _app = builder.Build();
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseMiddleware<TwoFactorEnforcementMiddleware>();
        _app.UseAntiforgery();
        _app.MapAccountEndpoints();

        // Test only: the cookie a completed password + TOTP sign-in gets (SignInManager.TwoFactorSignInAsync signs in with amr=mfa).
        _app.MapPost("/test/signin/{userId:guid}", async (Guid userId, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByIdAsync(userId.ToString());
            await signIn.SignInWithClaimsAsync(user!, isPersistent: false, [new Claim("amr", "mfa")]);
            return Results.Ok();
        }).AllowAnonymous();
        _app.MapGet("/test/whoami", (HttpContext context) => Results.Ok(new
        {
            Id = context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            Stamp = context.User.FindFirstValue(TwoFactorGate.SecurityStampClaimType)
        })).RequireAuthorization();
        _app.MapGet("/test/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { Token = antiforgery.GetAndStoreTokens(context).RequestToken })).RequireAuthorization();

        await _app.StartAsync();
        Client = _app.GetTestClient();
        Client.BaseAddress = new Uri("https://localhost/");
        return _app.Services;
    }

    public override async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await Database.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class AccountHostCollection : ICollectionFixture<AccountHostFixture>
{
    public const string Name = "web-account-host";
}

/// <summary>
/// A session cookie from before an admin reset the user's two-factor authentication is still accepted by the cookie handler for up
/// to 5 minutes (SecurityStampValidatorOptions.ValidationInterval). In that window it must not enrol an authenticator through
/// POST /api/account/setup-2fa, because RefreshSignInAsync would re-issue it with the new security stamp and the session would
/// survive the reset with the holder's authenticator as the account's second factor.
/// </summary>
[Collection(AccountHostCollection.Name)]
public sealed class StaleSessionTests
{
    public const string AuthCookie = "__Host-fleeto-auth";
    public const string AntiforgeryCookie = "__Host-fleeto-af";
    private readonly AccountHostFixture _fixture;

    public StaleSessionTests(AccountHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_session_from_before_a_two_factor_reset_cannot_enrol_an_authenticator()
    {
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var oldCookie = await SignInAsync(user.Id);
        var stampBefore = (await WhoAmIAsync(oldCookie))!.Value.GetProperty("stamp").GetString();

        var reset = await _fixture.Services.GetRequiredService<UserAdminService>().ResetTwoFactorAsync(WebFixtureBase.Admin(), user.Id);
        Assert.True(reset.Success, reset.Problem);
        Assert.NotEqual(stampBefore, await StampAsync(user.Id));

        // One minute later the cookie handler still accepts the old cookie (the window the stamp check closes).
        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(stampBefore, (await WhoAmIAsync(oldCookie))!.Value.GetProperty("stamp").GetString());

        // Its holder knows a key (the worst case) and posts a valid code for it with a valid antiforgery token.
        var key = await AuthenticatorKeyAsync(user.Id);
        var setup = await PostSetupAsync(oldCookie, key);

        // Refused and signed out: no fresh session cookie, two-factor authentication stays off.
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        Assert.Equal("/account/login", setup.Headers.Location?.OriginalString);
        Assert.Null(CookieFrom(setup, AuthCookie));
        Assert.False(await TwoFactorEnabledAsync(user.Id));

        // After the validation interval the old cookie is refused by the cookie handler as well.
        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await WhoAmIAsync(oldCookie));
    }

    [Fact]
    public async Task A_session_with_the_current_stamp_enrols_an_authenticator_and_keeps_its_session()
    {
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var reset = await _fixture.Services.GetRequiredService<UserAdminService>().ResetTwoFactorAsync(WebFixtureBase.Admin(), user.Id);
        Assert.True(reset.Success, reset.Problem);

        // Signed in again after the reset: the session carries the current stamp.
        var cookie = await SignInAsync(user.Id);
        var key = await AuthenticatorKeyAsync(user.Id);
        var setup = await PostSetupAsync(cookie, key);

        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        Assert.Equal("/account/recovery-codes", setup.Headers.Location?.OriginalString);
        Assert.True(await TwoFactorEnabledAsync(user.Id));
        var refreshed = CookieFrom(setup, AuthCookie);
        Assert.NotNull(refreshed);

        // Enabling two-factor changed the stamp; the re-issued cookie carries it and outlives the validation interval.
        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(10));
        var after = await WhoAmIAsync(refreshed!);
        Assert.NotNull(after);
        Assert.Equal(await StampAsync(user.Id), after!.Value.GetProperty("stamp").GetString());
    }

    private async Task<string> SignInAsync(Guid userId)
    {
        var signIn = await _fixture.Client.PostAsync($"/test/signin/{userId}", null);
        var cookie = CookieFrom(signIn, AuthCookie);
        Assert.NotNull(cookie);
        return cookie!;
    }

    private async Task<HttpResponseMessage> PostSetupAsync(string authCookie, string key)
    {
        var afResponse = await SendAsync(HttpMethod.Get, "/test/antiforgery", authCookie);
        Assert.Equal(HttpStatusCode.OK, afResponse.StatusCode);
        var afCookie = CookieFrom(afResponse, AntiforgeryCookie);
        var afToken = JsonDocument.Parse(await afResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        return await SendAsync(HttpMethod.Post, "/api/account/setup-2fa", authCookie, afCookie, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = Totp(key, DateTimeOffset.UtcNow), ["Setup"] = "0", ["__RequestVerificationToken"] = afToken
        }));
    }

    private async Task<string> AuthenticatorKeyAsync(Guid userId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await AccountService.GetOrCreateAuthenticatorKeyAsync(users, scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>(),
            (await users.FindByIdAsync(userId.ToString()))!);
    }

    private async Task<string?> StampAsync(Guid userId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.GetSecurityStampAsync((await users.FindByIdAsync(userId.ToString()))!);
    }

    private async Task<bool> TwoFactorEnabledAsync(Guid userId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.GetTwoFactorEnabledAsync((await users.FindByIdAsync(userId.ToString()))!);
    }

    private static string? CookieFrom(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(name + "=", StringComparison.Ordinal))
                .Select(v => v.Split(';')[0][(name.Length + 1)..]).LastOrDefault(v => v.Length > 0)
            : null;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string authCookie, string? antiforgeryCookie = null,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("Cookie", $"{AuthCookie}={authCookie}" + (antiforgeryCookie is null ? string.Empty : $"; {AntiforgeryCookie}={antiforgeryCookie}"));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<JsonElement?> WhoAmIAsync(string authCookie)
    {
        var response = await SendAsync(HttpMethod.Get, "/test/whoami", authCookie);
        return response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement : null;
    }

    /// <summary>RFC 6238 (SHA-1, 30 s, 6 digits) on the base32 key, as an authenticator app computes it.</summary>
    private static string Totp(string base32Key, DateTimeOffset at)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Key.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(at.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
