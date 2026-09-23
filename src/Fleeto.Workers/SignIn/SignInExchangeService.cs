using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.SignIn;

/// <summary>
/// Finishes the sign-ins with Microsoft Entra ID that web started (0.5.0). Only the workers are on the egress network, so
/// the authorization code is exchanged here: web writes the code and the PKCE verifier encrypted in a
/// <see cref="SignInExchange"/> row, and this loop trades them for an id_token, validates it and writes back the claims
/// Fleeto keeps — never a token, and never the client secret, which it reads from the settings itself.
/// <para>
/// Rows that nobody finished (a browser closed halfway) are deleted once they are older than
/// <see cref="EntraSignIn.ExchangeLifetime"/>, so a code never lingers.
/// </para>
/// </summary>
public sealed class SignInExchangeService : WorkerLoop
{
    /// <summary>At most this many exchanges per pass; a sign-in that has to wait is picked up by the next pass at once.</summary>
    private const int BatchSize = 20;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly SettingsStore _settings;
    private readonly ISecretProtector _protector;
    private readonly EntraSignInClientFactory _clients;
    private readonly ILogger<SignInExchangeService> _logger;
    private IDisposable? _subscription;

    public SignInExchangeService(IFleetoDbContextFactory dbFactory, INotificationBus bus, SettingsStore settings, ISecretProtector protector,
        EntraSignInClientFactory clients, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<SignInExchangeService> logger)
        : base("sign-ins", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _settings = settings;
        _protector = protector;
        _clients = clients;
        _logger = logger;
    }

    // A sign-in is finished on the notification; the interval only cleans up what was abandoned.
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.SignIns, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await ExchangeWaitingAsync(cancellationToken);
        return false;
    }

    /// <summary>Runs the work of one pass: every sign-in that waits, and a clean-up of the ones nobody finished.</summary>
    public async Task ExchangeWaitingAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var cutoff = now - EntraSignIn.ExchangeLifetime;

        var abandoned = await db.SignInExchanges.Where(e => e.CreatedAt < cutoff).ToListAsync(cancellationToken);
        if (abandoned.Count > 0)
        {
            db.SignInExchanges.RemoveRange(abandoned);
            await db.SaveChangesAsync(cancellationToken);
        }

        var waiting = await db.SignInExchanges
            .Where(e => e.State == SignInExchangeState.Requested && e.CreatedAt >= cutoff)
            .OrderBy(e => e.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return;
        }

        var settings = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken);
        if (settings is null || !settings.IsUsable)
        {
            foreach (var exchange in waiting)
            {
                Fail(exchange, "Signing in with Microsoft Entra ID is switched off for this instance. Sign in with a local account.", now);
            }

            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        using var client = _clients.Create(settings);
        foreach (var exchange in waiting)
        {
            await CompleteAsync(client, exchange, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CompleteAsync(EntraSignInClient client, SignInExchange exchange, DateTime now, CancellationToken cancellationToken)
    {
        SignInExchangeRequest request;
        try
        {
            request = SignInExchangeContents.UnprotectRequest(_protector, exchange.Id, exchange.EncryptedRequest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The sign-in request of exchange {Exchange} could not be read", exchange.Id);
            Fail(exchange, "The sign-in could not be read. Start it again.", now);
            return;
        }

        var outcome = await client.ExchangeAsync(request.Code, request.CodeVerifier, exchange.RedirectUri, cancellationToken);
        if (outcome.Claims is null)
        {
            Fail(exchange, outcome.Refusal ?? "The sign-in could not be finished. Try again.", now);
            return;
        }

        exchange.EncryptedClaims = SignInExchangeContents.ProtectClaims(_protector, exchange.Id, outcome.Claims);
        exchange.State = SignInExchangeState.Completed;
        exchange.CompletedAt = now;
    }

    private static void Fail(SignInExchange exchange, string reason, DateTime now)
    {
        exchange.State = SignInExchangeState.Failed;
        exchange.FailureReason = reason.Length > 500 ? reason[..500] : reason;
        exchange.CompletedAt = now;
    }
}
