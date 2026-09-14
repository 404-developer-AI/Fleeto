using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Identity;
using Fleetify.Web.Security;
using Fleetify.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Account;

/// <summary>
/// Sign-in, two-factor, sign-out and first-admin setup as plain HTML form posts. Form posts rather than Blazor calls,
/// because only an HTTP response can set or clear the authentication cookie, and because the rate limiter and the real
/// client IP apply to HTTP requests, not to circuit messages.
/// </summary>
public static class AccountEndpoints
{
    public const string AuthRateLimitPolicy = "auth";
    public const string DownloadRateLimitPolicy = "agent-download";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/account/login", LoginAsync)
            .AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy).ValidateAntiforgery("/account/login?error=expired").WithAnonymousPrincipal();

        app.MapPost("/api/account/two-factor", TwoFactorAsync)
            .AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy).ValidateAntiforgery("/account/login?error=twofactorexpired").WithAnonymousPrincipal();

        app.MapPost("/api/account/logout", LogoutAsync)
            .AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy).ValidateAntiforgery("/");

        app.MapPost("/api/account/setup-2fa", SetupTwoFactorAsync)
            .RequireAuthorization().RequireRateLimiting(AuthRateLimitPolicy).ValidateAntiforgery("/account/setup-2fa?error=expired");

        app.MapPost("/api/setup", SetupFirstAdminAsync)
            .AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy).ValidateAntiforgery("/setup?error=expired").WithAnonymousPrincipal();

        return app;
    }

    private static async Task<IResult> LoginAsync(HttpContext context, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
        IAuditLog audit, TimeProvider time)
    {
        var form = await context.Request.ReadFormAsync();
        var email = form["Email"].ToString().Trim();
        var password = form["Password"].ToString();
        var returnUrl = EndpointExtensions.SafeReturnUrl(form["ReturnUrl"].ToString());
        var returnQuery = returnUrl == "/" ? string.Empty : "&returnUrl=" + Uri.EscapeDataString(returnUrl);

        if (email.Length is 0 or > 320 || password.Length is 0 or > AccountService.MaximumPasswordLength)
        {
            return Results.Redirect("/account/login?error=invalid" + returnQuery);
        }

        var user = await users.FindByEmailAsync(email);
        if (user is not null)
        {
            // lockoutOnFailure: repeated wrong passwords lock the account (5 attempts, 15 minutes).
            var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                // Succeeded means the account has no two-factor authentication yet: the session is restricted to the setup
                // page until it is done (TwoFactorEnforcementMiddleware).
                await RecordLoginAsync(users, user, time);
                await audit.WriteAsync(UserRecord(AuditActions.LoginSucceeded, user, context, new { TwoFactor = "not set up" }));
                return Results.Redirect("/account/setup-2fa");
            }

            if (result.RequiresTwoFactor)
            {
                return Results.Redirect("/account/two-factor" + (returnUrl == "/" ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
            }

            await audit.WriteAsync(UserRecord(AuditActions.LoginFailed, user, context,
                new { Reason = result.IsLockedOut ? "locked out" : result.IsNotAllowed ? "not allowed" : "wrong password" }));
            if (result.IsLockedOut)
            {
                return Results.Redirect("/account/login?error=lockedout");
            }
        }
        else
        {
            await audit.WriteAsync(new AuditRecord(AuditActions.LoginFailed, "User", "unknown", null, AuditActorType.User, "unknown",
                email.Length > 200 ? email[..200] : email, new { Reason = "unknown email address" }, context.RemoteIp()));
        }

        // The same answer for an unknown address and a wrong password, so the form does not reveal which accounts exist.
        return Results.Redirect("/account/login?error=invalid" + returnQuery);
    }

    private static async Task<IResult> TwoFactorAsync(HttpContext context, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
        IAuditLog audit, TimeProvider time)
    {
        var form = await context.Request.ReadFormAsync();
        var code = AccountService.NormalizeCode(form["Code"].ToString());
        var isRecoveryCode = form["IsRecoveryCode"].ToString() is "true" or "on";
        var returnUrl = EndpointExtensions.SafeReturnUrl(form["ReturnUrl"].ToString());

        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return Results.Redirect("/account/login?error=twofactorexpired");
        }

        if (code.Length is 0 or > 64)
        {
            return Results.Redirect("/account/two-factor?error=invalid");
        }

        var result = isRecoveryCode
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);

        if (result.Succeeded)
        {
            await RecordLoginAsync(users, user, time);
            await audit.WriteAsync(UserRecord(AuditActions.LoginSucceeded, user, context,
                new { TwoFactor = isRecoveryCode ? "recovery code" : "authenticator app" }));
            return Results.Redirect(returnUrl);
        }

        await audit.WriteAsync(UserRecord(AuditActions.LoginFailed, user, context,
            new { Reason = result.IsLockedOut ? "locked out" : isRecoveryCode ? "wrong recovery code" : "wrong two-factor code" }));
        return result.IsLockedOut ? Results.Redirect("/account/login?error=lockedout") : Results.Redirect("/account/two-factor?error=invalid");
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
        IAuditLog audit)
    {
        if (context.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(context.User) is { } user)
        {
            await audit.WriteAsync(UserRecord(AuditActions.Logout, user, context));
        }

        await signIn.SignOutAsync();
        return Results.Redirect("/account/login?signedout=1");
    }

    private static async Task<IResult> SetupTwoFactorAsync(HttpContext context, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
        IAuditLog audit)
    {
        var user = await users.GetUserAsync(context.User);
        if (user is null)
        {
            await signIn.SignOutAsync();
            return Results.Redirect("/account/login");
        }

        var form = await context.Request.ReadFormAsync();
        var fromSetup = form["Setup"].ToString() == "1";
        var setupQuery = fromSetup ? "setup=1" : string.Empty;
        if (await users.GetTwoFactorEnabledAsync(user))
        {
            return Results.Redirect("/");
        }

        var code = AccountService.NormalizeCode(form["Code"].ToString());
        var valid = code.Length is > 0 and <= 10 &&
                    await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            await audit.WriteAsync(UserRecord(AuditActions.LoginFailed, user, context, new { Reason = "wrong code during two-factor setup" }));
            return Results.Redirect("/account/setup-2fa?error=invalid" + (fromSetup ? "&" + setupQuery : string.Empty));
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        // The recovery codes are generated and shown once by the next page, so they never travel in a URL or cookie.
        await users.SetAuthenticationTokenAsync(user, AccountService.MarkerProvider, AccountService.RecoveryCodesPendingToken, "1");
        await audit.WriteAsync(UserRecord(AuditActions.TwoFactorEnabled, user, context));

        // Enabling two-factor changed the security stamp; re-issue the cookie so this session survives validation.
        await signIn.RefreshSignInAsync(user);
        return Results.Redirect("/account/recovery-codes" + (fromSetup ? "?" + setupQuery : string.Empty));
    }

    private static async Task<IResult> SetupFirstAdminAsync(HttpContext context, SetupService setup, SignInManager<ApplicationUser> signIn,
        ILoggerFactory loggerFactory)
    {
        var form = await context.Request.ReadFormAsync();
        var token = form["Token"].ToString();
        var tokenQuery = "token=" + Uri.EscapeDataString(token.Length > 200 ? string.Empty : token);

        SetupResult result;
        try
        {
            result = await setup.CompleteAsync(token, form["DisplayName"], form["Email"], form["Password"], form["ConfirmPassword"],
                context.RemoteIp(), context.RequestAborted);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Fleetify.Web.Account.Setup").LogError(ex, "First-admin setup failed");
            return Results.Redirect("/setup?error=failed&" + tokenQuery);
        }

        if (result.State == SetupTokenState.AdminExists)
        {
            return Results.Redirect("/setup");
        }

        if (result.State != SetupTokenState.Valid)
        {
            return Results.Redirect("/setup?error=" + result.State.ToString().ToLowerInvariant() + "&" + tokenQuery);
        }

        if (!result.Success)
        {
            return Results.Redirect("/setup?problem=" + result.Problem.ToString().ToLowerInvariant() + "&" + tokenQuery);
        }

        await signIn.SignInAsync(result.User!, isPersistent: false);
        return Results.Redirect("/account/setup-2fa?setup=1");
    }

    private static async Task RecordLoginAsync(UserManager<ApplicationUser> users, ApplicationUser user, TimeProvider time)
    {
        user.LastLoginAt = time.GetUtcNow().UtcDateTime;
        await users.UpdateAsync(user);
    }

    private static AuditRecord UserRecord(string action, ApplicationUser user, HttpContext context, object? details = null) =>
        new(action, "User", user.Id.ToString(), null, AuditActorType.User, user.Id.ToString(),
            string.IsNullOrEmpty(user.DisplayName) ? user.Email ?? string.Empty : user.DisplayName, details, context.RemoteIp());
}

/// <summary>Anonymous operational endpoints: health and the agent download.</summary>
public static class OperationalEndpoints
{
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder app)
    {
        // Verifies the database connection: an app that cannot reach PostgreSQL is not healthy, whatever Kestrel says.
        app.MapGet("/health", async (IFleetifyDbContextFactory dbFactory) =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await using var db = dbFactory.CreateSystem();
                if (await db.Database.CanConnectAsync(timeout.Token))
                {
                    return Results.Ok(new { status = "healthy" });
                }
            }
            catch (Exception)
            {
                // Anonymous endpoint: no detail beyond healthy or unhealthy.
            }

            return Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/agent/download/windows-amd64", (IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            var configured = configuration["Agent:BinariesDirectory"];
            if (string.IsNullOrWhiteSpace(configured))
            {
                return NotAvailable();
            }

            var directory = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
            // The image lays binaries out per platform (windows-amd64/); a flat directory works as well.
            var path = new[] { Path.Combine(directory, "windows-amd64", "fleetify-agent.exe"), Path.Combine(directory, "fleetify-agent.exe") }
                .FirstOrDefault(File.Exists);
            if (path is null)
            {
                return NotAvailable();
            }

            return Results.File(path, "application/vnd.microsoft.portable-executable", "fleetify-agent.exe", enableRangeProcessing: true);
        }).AllowAnonymous().RequireRateLimiting(AccountEndpoints.DownloadRateLimitPolicy);

        return app;

        static IResult NotAvailable() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
            detail: "The agent binary is not available on this server.");
    }
}
