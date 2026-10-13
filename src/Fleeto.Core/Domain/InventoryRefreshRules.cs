namespace Fleeto.Core.Domain;

/// <summary>
/// Refresh on an endpoint (0.6.0): the page reloads, and a live agent is asked for a fresh inventory at most once per interval
/// per endpoint. The gateway only asks while the request is recent, so a request that waited for a reconnect is not sent late.
/// </summary>
public static class InventoryRefreshRules
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>How long after the request the gateway still asks the agent (after a lost notification).</summary>
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromMinutes(5);
}
