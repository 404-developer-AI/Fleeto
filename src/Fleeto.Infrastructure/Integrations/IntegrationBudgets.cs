namespace Fleeto.Infrastructure.Integrations;

/// <summary>
/// The request budget of one Action1 enterprise (0.4.0). Action1 counts every call of its whole API against one budget
/// per enterprise and recommends staying under 30 a minute, so Fleeto stays at 20.
/// <para>
/// The budget lives in the workers, because they are the only containers with outbound access: web is on a network
/// without NAT and cannot reach the internet at all (<c>deploy/compose/compose.yml</c>). What an admin triggers in
/// Settings is a request web writes to the database; the workers make the call.
/// </para>
/// </summary>
public static class IntegrationBudgets
{
    /// <summary>What the workers may spend, which is everything Fleeto spends.</summary>
    public const int WorkerRequestsPerMinute = 20;
}
