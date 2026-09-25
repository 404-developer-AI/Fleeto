using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Account;

/// <summary>
/// Signing in with Microsoft Entra ID (0.5.0): the browser is sent to Microsoft and comes back here with an authorization
/// code. Web cannot reach Microsoft itself, so the code is handed to the workers through a <see cref="SignInExchange"/> row
/// and this endpoint waits for the claims they validated (ARCHITECTURE §1 and §5).
/// <para>
/// What the sign-in needs meanwhile — the state, the nonce and the PKCE verifier — travels in one encrypted cookie, so a
/// code that comes back without the browser that started it is worth nothing. The person signing in always ends up with the
/// local authenticator step; why an attempt was refused is in the audit log, never in the browser, so the page does not say
/// which accounts exist.
/// </para>
/// </summary>
public static class EntraSignInEndpoints
{
    public const string CookieName = "__Host-fleeto-entra";

    /// <summary>How a sign-in through Entra ID is named in the audit log.</summary>
    public const string EntraRoute = "entra";

    /// <summary>Purpose of the data protector that seals the cookie; changing it invalidates sign-ins in flight.</summary>
    private const string ProtectorPurpose = "Fleeto.Web.Account.EntraSignIn.v1";

    /// <summary>How often the row of a waiting sign-in is read while the workers finish it.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static IEndpointRouteBuilder MapEntraSignInEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/account/entra/start", StartAsync)
            .AllowAnonymous().RequireRateLimiting(AccountEndpoints.AuthRateLimitPolicy)
            .ValidateAntiforgery("/account/login?error=expired").WithAnonymousPrincipal();

        app.MapGet(EntraSignIn.CallbackPath, CallbackAsync)
            .AllowAnonymous().RequireRateLimiting(AccountEndpoints.AuthRateLimitPolicy).WithAnonymousPrincipal();

        return app;
    }

    private static async Task<IResult> StartAsync(HttpContext context, SettingsStore settings, IFleetoDbContextFactory dbFactory,
        IDataProtectionProvider protection)
    {
        var form = await context.Request.ReadFormAsync();
        var returnUrl = EndpointExtensions.SafeReturnUrl(form["ReturnUrl"].ToString());

        var configured = await settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, context.RequestAborted);
        if (configured is null || !configured.IsUsable)
        {
            return Results.Redirect("/account/login?error=entraoff");
        }

        await using var db = dbFactory.CreateSystem();
        var instance = await InstanceQueries.GetInstanceAsync(db, context.RequestAborted);
        var redirectUri = EntraSignIn.RedirectUri(instance.WebBaseUrl);

        var state = EntraSignIn.NewRandomValue();
        var nonce = EntraSignIn.NewRandomValue();
        var verifier = EntraSignIn.NewRandomValue();
        var started = string.Join('\n', state, nonce, verifier, returnUrl);
        var cookieValue = protection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector().Protect(started, EntraSignIn.ExchangeLifetime);

        context.Response.Cookies.Append(CookieName, cookieValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            // Lax: Microsoft sends the browser back with a top-level GET, which Lax allows, while a cross-site post does not
            // carry the cookie.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = EntraSignIn.ExchangeLifetime
        });

        return Results.Redirect(EntraSignIn.AuthorizeUrl(configured, redirectUri, state, nonce, EntraSignIn.CodeChallenge(verifier)));
    }

    private static async Task<IResult> CallbackAsync(HttpContext context, SettingsStore settings, IFleetoDbContextFactory dbFactory,
        IDataProtectionProvider protection, ISecretProtector protector, INotificationBus bus, SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users, IAuditLog audit, TimeProvider time, ILoggerFactory loggerFactory)
    {
        var pending = ReadCookie(context, protection);
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = true, SameSite = SameSiteMode.Lax });
        if (pending is null)
        {
            return Results.Redirect("/account/login?error=entrastate");
        }

        if (context.Request.Query.ContainsKey("error"))
        {
            // Microsoft refused or the person cancelled; its own words go to the log, never to the page.
            loggerFactory.CreateLogger("Fleeto.Web.Account.EntraSignIn")
                .LogInformation("Microsoft ended a sign-in with {Error}", LogText.Clean(context.Request.Query["error"].ToString(), 100));
            await FailAsync(audit, context, "Microsoft ended the sign-in", null);
            return Results.Redirect("/account/login?error=entra");
        }

        var state = context.Request.Query["state"].ToString();
        var code = context.Request.Query["code"].ToString();
        if (!FixedTimeEquals(state, pending.State) || code.Length is 0 or > 4000)
        {
            await FailAsync(audit, context, "The answer did not match the sign-in that was started", null);
            return Results.Redirect("/account/login?error=entrastate");
        }

        var configured = await settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, context.RequestAborted);
        if (configured is null || !configured.IsUsable)
        {
            return Results.Redirect("/account/login?error=entraoff");
        }

        await using var db = dbFactory.CreateSystem();
        var instance = await InstanceQueries.GetInstanceAsync(db, context.RequestAborted);
        var now = time.GetUtcNow().UtcDateTime;
        var exchange = new SignInExchange
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            RedirectUri = EntraSignIn.RedirectUri(instance.WebBaseUrl),
            State = SignInExchangeState.Requested
        };
        exchange.EncryptedRequest = SignInExchangeContents.ProtectRequest(protector, exchange.Id, new SignInExchangeRequest(code, pending.Verifier));
        db.SignInExchanges.Add(exchange);
        await db.SaveChangesAsync(context.RequestAborted);
        await bus.PublishAsync(NotificationChannels.SignIns, exchange.Id.ToString("D"), context.RequestAborted);

        var finished = await WaitForAsync(db, exchange.Id, time, context.RequestAborted);
        SignInClaims? claims = null;
        if (finished is { State: SignInExchangeState.Completed, EncryptedClaims: { } stored })
        {
            try
            {
                claims = SignInExchangeContents.UnprotectClaims(protector, exchange.Id, stored);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Fleeto.Web.Account.EntraSignIn").LogError(ex, "The claims of sign-in {Exchange} could not be read", exchange.Id);
            }
        }

        // The row has done its work; the code and the claims never outlive the sign-in.
        if (finished is not null)
        {
            db.SignInExchanges.Remove(finished);
            await db.SaveChangesAsync(context.RequestAborted);
        }

        if (claims is null)
        {
            await FailAsync(audit, context, finished?.FailureReason ?? "The sign-in did not finish in time", null);
            return Results.Redirect("/account/login?error=entra");
        }

        if (!FixedTimeEquals(claims.Nonce ?? string.Empty, pending.Nonce))
        {
            await FailAsync(audit, context, "The sign-in did not carry the nonce of this browser", claims.Account);
            return Results.Redirect("/account/login?error=entrastate");
        }

        // What Microsoft says about how the person signed in, whichever way the sign-in continues: the claims that decide
        // whether Fleeto asks its own authenticator code, so an admin can see why it did.
        loggerFactory.CreateLogger("Fleeto.Web.Account.EntraSignIn").LogInformation(
            "A sign-in with Microsoft Entra ID came in with amr {Amr}, acr {Acr} and acrs {Acrs}; multi-factor authentication proven: {MfaProven}",
            Describe(claims.Methods), claims.AuthenticationClass ?? "none", Describe(claims.AuthenticationContexts), claims.MfaProven);

        if (!Guid.TryParse(claims.ObjectId, out var objectId))
        {
            await FailAsync(audit, context, "The sign-in did not name the account", claims.Account);
            return Results.Redirect("/account/login?error=entra");
        }

        var user = await users.Users.SingleOrDefaultAsync(u => u.EntraObjectId == objectId, context.RequestAborted);
        if (user is null)
        {
            await FailAsync(audit, context, "No user of this instance is linked to that Entra ID account", claims.Account);
            return Results.Redirect("/account/login?error=entra");
        }

        if (user.EntraTenantId is { Length: > 0 } linkedTenant && !string.Equals(linkedTenant, claims.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            await FailAsync(audit, context, "The account belongs to another tenant than the link was made for", claims.Account);
            return Results.Redirect("/account/login?error=entra");
        }

        if (await users.IsLockedOutAsync(user) || !await signIn.CanSignInAsync(user))
        {
            await FailAsync(audit, context, "The account is locked out or not allowed to sign in", claims.Account);
            return Results.Redirect("/account/login?error=lockedout");
        }

        // The tenant the sign-in proved, and the account name for Settings; the link itself stays the object id.
        if (user.EntraTenantId is null or "")
        {
            user.EntraTenantId = claims.TenantId;
        }

        if (claims.Account is { Length: > 0 } account && !string.Equals(user.EntraAccount, account, StringComparison.OrdinalIgnoreCase))
        {
            user.EntraAccount = account;
        }

        // Only a sign-in that finishes here counts as a sign-in; when the authenticator code is still to come, that step
        // records it, exactly as it does after a password.
        var twoFactorEnabled = await users.GetTwoFactorEnabledAsync(user);
        var secondFactor = EntraSignIn.SecondFactorSource(claims, configured);
        if (secondFactor is not null || !twoFactorEnabled)
        {
            user.LastLoginAt = now;
        }

        await users.UpdateAsync(user);

        if (secondFactor is not null)
        {
            // Either the token says Microsoft asked for a second factor (amr), or the customer leaves the second factor of
            // linked users to its tenant (Settings, Sign-in). Fleeto does not ask for one on top. The session carries which
            // of the two it was; TwoFactorGate checks the link again on every request, so unlinking the user ends the
            // exemption, and switching the setting off ends the sessions of linked users.
            await signIn.SignInWithClaimsAsync(user, isPersistent: false, [new Claim(TwoFactorGate.SecondFactorClaim, secondFactor)]);
            await audit.WriteAsync(SuccessRecord(user, context, claims,
                secondFactor == EntraSignIn.ProvenSecondFactor ? "Microsoft Entra ID" : "left to Microsoft"));
            return Results.Redirect(pending.ReturnUrl);
        }

        if (!twoFactorEnabled)
        {
            // The token proves one factor and the user has no authenticator yet: the session is restricted to the setup
            // page, exactly as after a first password sign-in.
            await signIn.SignInAsync(user, isPersistent: false);
            await audit.WriteAsync(SuccessRecord(user, context, claims, "not set up"));
            return Results.Redirect("/account/setup-2fa");
        }

        // The token does not prove multi-factor authentication, so the local authenticator code is asked on top of it; the
        // entry in the audit log is written when that code is accepted.
        var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, EntraRoute));
        await context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(pending.ReturnUrl == "/"
            ? "/account/two-factor"
            : "/account/two-factor?returnUrl=" + Uri.EscapeDataString(pending.ReturnUrl));
    }

    /// <summary>Reads the row of the sign-in until the workers finished it, or until waiting is pointless.</summary>
    private static async Task<SignInExchange?> WaitForAsync(FleetoDbContext db, Guid exchangeId, TimeProvider time, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + EntraSignIn.ExchangeWait;
        while (true)
        {
            var exchange = await db.SignInExchanges.SingleOrDefaultAsync(e => e.Id == exchangeId, cancellationToken);
            if (exchange is null || exchange.State != SignInExchangeState.Requested)
            {
                return exchange;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return exchange;
            }

            await Task.Delay(PollInterval, time, cancellationToken);
            // The row was read in this context before; forget it, so the next read sees what the workers wrote.
            db.Entry(exchange).State = EntityState.Detached;
        }
    }

    private static PendingSignIn? ReadCookie(HttpContext context, IDataProtectionProvider protection)
    {
        if (context.Request.Cookies[CookieName] is not { Length: > 0 and < 4000 } sealedValue)
        {
            return null;
        }

        try
        {
            var parts = protection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector().Unprotect(sealedValue).Split('\n');
            return parts.Length == 4 ? new PendingSignIn(parts[0], parts[1], parts[2], EndpointExtensions.SafeReturnUrl(parts[3])) : null;
        }
        catch (CryptographicException)
        {
            // Expired, tampered with, or sealed by keys this instance no longer has.
            return null;
        }
    }

    private static AuditRecord SuccessRecord(ApplicationUser user, HttpContext context, SignInClaims claims, string twoFactor) =>
        new(AuditActions.LoginSucceeded, "User", user.Id.ToString(), null, AuditActorType.User, user.Id.ToString(),
            string.IsNullOrEmpty(user.DisplayName) ? user.Email ?? string.Empty : user.DisplayName,
            new
            {
                Route = EntraRoute,
                Account = claims.Account,
                claims.MfaProven,
                TwoFactor = twoFactor,
                Amr = Describe(claims.Methods),
                Acr = claims.AuthenticationClass ?? "none",
                Acrs = Describe(claims.AuthenticationContexts)
            }, context.RemoteIp());

    private static string Describe(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(",", values);

    private static Task FailAsync(IAuditLog audit, HttpContext context, string reason, string? account) =>
        audit.WriteAsync(new AuditRecord(AuditActions.LoginFailed, "User", "unknown", null, AuditActorType.User, "unknown",
            Trim(account, 200) ?? "unknown", new { Route = "entra", Reason = reason }, context.RemoteIp()));

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary>What the cookie of a started sign-in carries.</summary>
    private sealed record PendingSignIn(string State, string Nonce, string Verifier, string ReturnUrl);
}
