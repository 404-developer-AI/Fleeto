using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Fleeto.Infrastructure.Integrations.Action1;

/// <summary>
/// Builds an <see cref="Action1Client"/> for a stored integration row (0.4.0). The request budget is shared by every
/// client this factory makes, because Action1 counts its whole API against one budget per enterprise; one factory lives
/// per process, with the permits that process is allowed (see <see cref="RequestBudget"/>).
/// </summary>
public sealed class Action1ClientFactory
{
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly RequestBudget _budget;
    private readonly Func<HttpMessageHandler>? _handler;

    /// <param name="handler">
    /// A stand-in for the network, used by tests. Left out in every container, where the client builds its own hardened
    /// handler.
    /// </param>
    public Action1ClientFactory(ISecretProtector protector, TimeProvider time, ILoggerFactory loggers, int permitsPerMinute,
        Func<HttpMessageHandler>? handler = null)
    {
        _protector = protector;
        _time = time;
        _loggers = loggers;
        _budget = new RequestBudget(permitsPerMinute, time);
        _handler = handler;
    }

    /// <summary>The budget every client of this process shares, for logging and tests.</summary>
    public RequestBudget Budget => _budget;

    /// <summary>
    /// A client for this integration row, or null when the row is not an Action1 row, has no region or has no credentials
    /// that can be read (a root key that no longer matches, for instance).
    /// </summary>
    public Action1Client? TryCreate(Integration integration)
    {
        if (integration.Type != IntegrationType.Action1 || integration.Region is not { } region ||
            string.IsNullOrEmpty(integration.EncryptedCredentials))
        {
            return null;
        }

        Action1Credentials credentials;
        try
        {
            credentials = IntegrationCredentials.Unprotect(_protector, integration.Id, integration.EncryptedCredentials);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            _loggers.CreateLogger<Action1ClientFactory>().LogError(ex,
                "The stored Action1 credentials of integration {IntegrationId} could not be read", integration.Id);
            return null;
        }

        return new Action1Client(credentials, region, _budget, _time, _loggers.CreateLogger<Action1Client>(), _handler?.Invoke());
    }
}
