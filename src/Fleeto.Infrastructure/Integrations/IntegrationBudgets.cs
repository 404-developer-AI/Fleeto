namespace Fleeto.Infrastructure.Integrations;

/// <summary>
/// How the request budget of one Action1 enterprise is split over the containers that call it (0.4.0). Action1 counts
/// every call of its whole API against one budget per enterprise and recommends staying under 30 a minute, so Fleeto
/// stays under 20: the workers poll with most of it, web keeps enough for what an admin triggers by hand.
/// </summary>
public static class IntegrationBudgets
{
    /// <summary>What web may spend: a connection test and a list of organizations, at once and without waiting.</summary>
    public const int WebRequestsPerMinute = 5;

    /// <summary>What the workers may spend on polling.</summary>
    public const int WorkerRequestsPerMinute = 15;

    /// <summary>What Fleeto spends in total, well under Action1's recommendation of 30.</summary>
    public const int TotalRequestsPerMinute = WebRequestsPerMinute + WorkerRequestsPerMinute;
}
