using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.SignIn;

/// <summary>
/// Answers what admins ask Microsoft from Settings (0.5.0): a test of the app registration of the sign-in or of Graph email,
/// and a search of the users of the tenant to link or add. Only the workers are on the egress network, so web writes the
/// question as a <see cref="MicrosoftRequest"/> and this loop asks Microsoft with the credential it reads from the settings
/// itself; the answer holds no token and no secret. Rows older than <see cref="MicrosoftAnswers.Lifetime"/> are deleted,
/// so the names a search found do not linger.
/// </summary>
public sealed class MicrosoftRequestService : WorkerLoop
{
    private const int BatchSize = 10;
    private const string SignInPage = "Settings, Sign-in";
    private const string EmailPage = "Settings, Email";
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(5);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly SettingsStore _settings;
    private readonly MicrosoftGraphClient _graph;
    private IDisposable? _subscription;

    // The token of the sign-in registration, reused by searches while it is valid: an admin types, and every keystroke must
    // not cost a token request. Keyed by the whole credential, so a changed secret gets a new token at once.
    private (AppCredential Credential, GraphToken Token)? _searchToken;

    public MicrosoftRequestService(IFleetoDbContextFactory dbFactory, INotificationBus bus, SettingsStore settings, MicrosoftGraphClient graph,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<MicrosoftRequestService> logger)
        : base("microsoft-requests", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _settings = settings;
        _graph = graph;
    }

    // Answered on the notification; the interval only cleans up.
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.MicrosoftRequests, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) => await AnswerWaitingAsync(cancellationToken);

    /// <summary>Answers what waits and deletes what nobody read. True when more is waiting than one pass takes.</summary>
    public async Task<bool> AnswerWaitingAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var cutoff = now - MicrosoftAnswers.Lifetime;

        await db.MicrosoftRequests.Where(r => r.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

        var waiting = await db.MicrosoftRequests
            .Where(r => r.State == MicrosoftRequestState.Requested)
            .OrderBy(r => r.CreatedAt)
            .Take(BatchSize + 1)
            .ToListAsync(cancellationToken);

        foreach (var request in waiting.Take(BatchSize))
        {
            try
            {
                await AnswerAsync(request, cancellationToken);
            }
            catch (MicrosoftGraphException ex)
            {
                Fail(request, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Microsoft request {Request} ({Kind}) failed", request.Id, request.Kind);
                Fail(request, "Fleeto could not finish this. Try again in a minute.");
            }

            request.CompletedAt = Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        return waiting.Count > BatchSize;
    }

    private async Task AnswerAsync(MicrosoftRequest request, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case MicrosoftRequestKind.TestSignIn:
                Complete(request, MicrosoftAnswers.Serialize(await TestSignInAsync(cancellationToken)));
                break;
            case MicrosoftRequestKind.TestEmail:
                Complete(request, MicrosoftAnswers.Serialize(await TestEmailAsync(cancellationToken)));
                break;
            case MicrosoftRequestKind.SearchUsers:
                var credential = await SignInCredentialAsync(cancellationToken)
                                 ?? throw new MicrosoftGraphException("Configure the sign-in with Microsoft Entra ID in Settings, Sign-in first.");
                var token = await SearchTokenAsync(credential, cancellationToken);
                Complete(request, MicrosoftAnswers.Serialize(await _graph.SearchUsersAsync(token, request.Query, cancellationToken)));
                break;
            default:
                Fail(request, "Fleeto does not know this question.");
                break;
        }
    }

    /// <summary>The credential of the sign-in, the tenant check and whether users can be chosen from Microsoft.</summary>
    private async Task<IReadOnlyList<MicrosoftCheck>> TestSignInAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken);
        var credential = CredentialOf(settings);
        if (credential is null)
        {
            return [new(MicrosoftCheckStatus.Failed, "Save the tenant, the client ID and the client secret in Settings, Sign-in first.")];
        }

        var checks = new List<MicrosoftCheck>();
        GraphToken token;
        try
        {
            token = await _graph.GetTokenAsync(credential, SignInPage, cancellationToken);
        }
        catch (MicrosoftGraphException ex)
        {
            return [new(MicrosoftCheckStatus.Failed, ex.Message)];
        }

        _searchToken = (credential, token);
        checks.Add(new(MicrosoftCheckStatus.Ok, "Fleeto signed in to the tenant as the app registration: the tenant ID, client ID and client secret are correct."));

        checks.Add(await _graph.CanReadUsersAsync(token, cancellationToken)
            ? new(MicrosoftCheckStatus.Ok, $"{MicrosoftGraphClient.UserReadAll} is granted: users can be chosen from Microsoft in Settings, Users.")
            : new(MicrosoftCheckStatus.Warning,
                $"{MicrosoftGraphClient.UserReadAll} is not granted, so users cannot be chosen from Microsoft; linking by object ID still works. " +
                "Add it as an application permission of Microsoft Graph and grant admin consent."));

        checks.Add(TenantCheck(await _graph.ReadTenantMfaAsync(token, cancellationToken), settings!.MicrosoftHandlesSecondFactor));
        AddExtraRoles(checks, token, [MicrosoftGraphClient.UserReadAll, MicrosoftGraphClient.PolicyReadAll], "The sign-in");
        checks.Add(new(MicrosoftCheckStatus.Info,
            "The redirect URI and the sign-in permissions (openid, profile, email) cannot be checked from here. Sign in with Microsoft once to check them."));
        return checks;
    }

    /// <summary>The credential of Graph email and whether it may send; the mailbox itself only shows when a message is sent.</summary>
    private async Task<IReadOnlyList<MicrosoftCheck>> TestEmailAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken);
        if (settings is null || !settings.IsComplete)
        {
            return [new(MicrosoftCheckStatus.Failed, "Save the Microsoft Graph settings with a client secret or a certificate in Settings, Email first.")];
        }

        var certificate = settings.CredentialType == GraphCredentialType.Certificate;
        if (GraphMail.Credential(settings) is not { } credential)
        {
            return [new(MicrosoftCheckStatus.Failed, "No certificate is in use for Microsoft Graph. Create one in Settings, Email and switch to it.")];
        }

        GraphToken token;
        try
        {
            token = await _graph.GetTokenAsync(credential, EmailPage, cancellationToken);
        }
        catch (MicrosoftGraphException ex)
        {
            return [new(MicrosoftCheckStatus.Failed, ex.Message)];
        }

        var checks = new List<MicrosoftCheck>
        {
            new(MicrosoftCheckStatus.Ok,
                $"Fleeto signed in to the tenant as the app registration: the tenant ID, client ID and {(certificate ? "certificate" : "client secret")} are correct."),
            token.Roles.Contains(MicrosoftGraphClient.MailSend, StringComparer.OrdinalIgnoreCase)
                ? new(MicrosoftCheckStatus.Ok, $"{MicrosoftGraphClient.MailSend} is granted.")
                : new(MicrosoftCheckStatus.Failed,
                    $"{MicrosoftGraphClient.MailSend} is not granted, so Fleeto cannot send email. Add it as an application permission of Microsoft Graph and grant admin consent.")
        };
        AddExtraRoles(checks, token, [MicrosoftGraphClient.MailSend], "Email");
        checks.Add(new(MicrosoftCheckStatus.Info,
            $"Whether Fleeto may send as {settings.SenderAddress} depends on the Exchange application access policy, which only a message shows. Send a test email to check it."));
        return checks;
    }

    /// <summary>
    /// How the tenant asks for a second factor, as far as Fleeto may see it. Worse when the second factor of linked users is
    /// left to Microsoft (Settings, Sign-in), because then nothing else asks for one.
    /// </summary>
    private static MicrosoftCheck TenantCheck(TenantMfaState? tenant, bool leftToMicrosoft)
    {
        const string Switch = "\"Microsoft handles the second factor of linked users\"";
        return tenant switch
        {
            null => new(MicrosoftCheckStatus.Info,
                $"Fleeto cannot see whether your tenant asks for a second factor. Grant the application permission {MicrosoftGraphClient.PolicyReadAll} " +
                "with admin consent to let this test check Security Defaults and Conditional Access."),
            { SecurityDefaults: true } => new(MicrosoftCheckStatus.Ok,
                "Security Defaults is on: Microsoft asks for a second factor when it considers one necessary, not at every sign-in."),
            { EnabledConditionalAccessPolicies: > 0 and var count } => new(MicrosoftCheckStatus.Ok,
                $"{count} Conditional Access {(count == 1 ? "policy is" : "policies are")} on. Check that one requires multi-factor authentication for Fleeto."),
            { EnabledConditionalAccessPolicies: 0 } => new(leftToMicrosoft ? MicrosoftCheckStatus.Failed : MicrosoftCheckStatus.Warning,
                "Neither Security Defaults nor a Conditional Access policy is on, so Microsoft asks for no second factor. " +
                (leftToMicrosoft
                    ? $"Linked users now sign in to Fleeto with one factor: require multi-factor authentication in the tenant, or switch {Switch} off."
                    : $"Keep {Switch} off until the tenant requires multi-factor authentication.")),
            _ => new(MicrosoftCheckStatus.Info,
                "Security Defaults is off, and Fleeto could not read the Conditional Access policies (they need Entra ID P1). " +
                "Check in the tenant that multi-factor authentication is required.")
        };
    }

    /// <summary>Least privilege: a registration that can do more than Fleeto needs is worth a warning.</summary>
    private static void AddExtraRoles(List<MicrosoftCheck> checks, GraphToken token, IReadOnlyList<string> needed, string what)
    {
        var extra = token.Roles.Where(r => !needed.Contains(r, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
        {
            checks.Add(new(MicrosoftCheckStatus.Warning,
                $"The app registration also holds {string.Join(", ", extra)}. {what} needs only {string.Join(" and ", needed)}; remove the rest, so its credential can do no more than that."));
        }
    }

    private async Task<AppCredential?> SignInCredentialAsync(CancellationToken cancellationToken) =>
        CredentialOf(await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken));

    private static AppCredential? CredentialOf(EntraSignInSettings? settings) =>
        settings is { IsComplete: true } ? new AppCredential(settings.TenantId, settings.ClientId, settings.ClientSecret, null) : null;

    private async Task<GraphToken> SearchTokenAsync(AppCredential credential, CancellationToken cancellationToken)
    {
        if (_searchToken is { } cached && cached.Credential == credential && cached.Token.ExpiresAt - TokenMargin > Time.GetUtcNow())
        {
            return cached.Token;
        }

        var token = await _graph.GetTokenAsync(credential, SignInPage, cancellationToken);
        _searchToken = (credential, token);
        return token;
    }

    private static void Complete(MicrosoftRequest request, string resultJson)
    {
        request.State = MicrosoftRequestState.Completed;
        request.ResultJson = resultJson;
    }

    private static void Fail(MicrosoftRequest request, string reason)
    {
        request.State = MicrosoftRequestState.Failed;
        request.FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }
}
