namespace Fleeto.Core.Domain;

/// <summary>
/// The limits of patch deployments (0.4.0 step 3), shared by web, the workers and the tests.
///
/// Fleeto does not patch anything itself: it hands a deployment to the patch management product and follows it. The
/// numbers here keep one deployment from spending the request budget of the whole instance, and keep Fleeto from
/// claiming to know how a deployment ended when the product stopped telling it.
/// </summary>
public static class PatchRules
{
    /// <summary>Endpoints one deployment may target. A larger selection is refused rather than silently cut.</summary>
    public const int MaxEndpointsPerDeployment = 200;

    /// <summary>Updates a technician may choose in one deployment; beyond it, deploying everything missing is the answer.</summary>
    public const int MaxUpdatesPerDeployment = 100;

    /// <summary>How often a running deployment is asked about. Action1 has no webhooks, so following it means polling.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long Fleeto follows a deployment. After this the deployment is abandoned: an endpoint that was offline the whole
    /// time never reports, and a row that stays open forever would poll forever.
    /// </summary>
    public static readonly TimeSpan FollowFor = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the product keeps retrying an endpoint that it could not reach, in minutes. It matches
    /// <see cref="FollowFor"/>, so the product gives up at the same time Fleeto stops looking.
    /// </summary>
    public const int RetryMinutes = 1440;

    /// <summary>
    /// The message the product shows on a workstation before it restarts by itself. Fleeto sends its own wording so the
    /// text follows the tone of the rest of the product (branding §8).
    /// </summary>
    public const string RebootMessage =
        "Updates have been installed and this computer needs to restart to finish them. Save your work; it restarts automatically when the time is up.";

    /// <summary>
    /// Minutes the user gets before the product restarts the endpoint by itself. Action1 reads <c>timeout</c> in minutes
    /// (its OpenAPI document, RebootOptions); 0.4.0 sent seconds, which made a restart wait 30 hours.
    /// </summary>
    public const int RebootTimeoutMinutes = 30;
}
