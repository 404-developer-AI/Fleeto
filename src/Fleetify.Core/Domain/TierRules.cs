using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

/// <summary>Features that require a managed endpoint.</summary>
public enum ManagedFeature
{
    Policy,
    MonitoringTemplate,
    Checks,
    Alerts,
    Jobs,
    Scripts,
    Patching,
    RemoteControl
}

/// <summary>Thrown when an operation needs a managed endpoint and the endpoint is agent-only.</summary>
public sealed class TierRequiredException : InvalidOperationException
{
    public TierRequiredException(Guid endpointId, ManagedFeature feature)
        : base($"{FeatureName(feature)} is only available on managed endpoints. Switch the endpoint to managed first.")
    {
        EndpointId = endpointId;
        Feature = feature;
    }

    public Guid EndpointId { get; }
    public ManagedFeature Feature { get; }

    private static string FeatureName(ManagedFeature feature) => feature switch
    {
        ManagedFeature.RemoteControl => "Remote control",
        ManagedFeature.MonitoringTemplate => "A monitoring template",
        _ => feature.ToString()
    };
}

/// <summary>
/// Tier enforcement in the domain layer, one of four layers (domain, signer, gateway, agent). Every
/// operation on a managed-only feature calls <see cref="EnsureManaged"/> with the endpoint as stored in
/// the database, never with a tier taken from user input.
/// </summary>
public static class TierRules
{
    public static void EnsureManaged(Endpoint endpoint, ManagedFeature feature)
    {
        if (!IsManaged(endpoint.Tier))
        {
            throw new TierRequiredException(endpoint.Id, feature);
        }
    }

    public static bool IsManaged(EndpointTier tier) => tier == EndpointTier.Managed;

    /// <summary>
    /// The tier the endpoint behaves as. After the license grace period every endpoint behaves as agent-only,
    /// whatever its stored tier. Nothing is deleted, so a new license restores the stored tiers.
    /// </summary>
    public static EndpointTier EffectiveTier(EndpointTier storedTier, LicenseStatus licenseStatus) =>
        licenseStatus.AllowsManaged ? storedTier : EndpointTier.AgentOnly;
}
