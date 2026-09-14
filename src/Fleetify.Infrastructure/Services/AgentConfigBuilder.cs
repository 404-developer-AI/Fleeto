using System.Security.Cryptography;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using ProtoCheckType = Fleetify.Protocol.Agent.V1.CheckType;
using ProtoTier = Fleetify.Protocol.Agent.V1.Tier;

namespace Fleetify.Infrastructure.Services;

/// <summary>
/// Computes the configuration an endpoint should run, from the database only. Used by the signer, which signs
/// what it computed itself rather than anything another container proposes.
/// </summary>
public sealed class AgentConfigBuilder
{
    private readonly LicenseService _licenses;

    public AgentConfigBuilder(LicenseService licenses)
    {
        _licenses = licenses;
    }

    public sealed record Built(AgentConfig Config, string ContentHash, Endpoint Endpoint);

    /// <summary>
    /// Builds the configuration without version and issue time. Returns null when the endpoint does not exist.
    /// The content hash covers everything else, so an unchanged configuration is not re-issued.
    /// </summary>
    public async Task<Built?> BuildAsync(FleetifyDbContext db, Guid endpointId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var licenseStatus = await _licenses.GetStatusAsync(db, cancellationToken);
        var effectiveTier = TierRules.EffectiveTier(endpoint.Tier, licenseStatus);

        var policy = await db.SitePolicies.AsNoTracking()
                         .Where(l => l.SiteId == endpoint.SiteId)
                         .Select(l => l.Policy)
                         .FirstOrDefaultAsync(cancellationToken)
                     ?? await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, cancellationToken)
                     ?? new Policy();

        var config = new AgentConfig
        {
            InstanceId = instanceId.ToString("D"),
            EndpointId = endpoint.Id.ToString("D"),
            Tier = effectiveTier == EndpointTier.Managed ? ProtoTier.Managed : ProtoTier.AgentOnly,
            HeartbeatIntervalSeconds = (uint)Math.Clamp(policy.HeartbeatIntervalSeconds, 10, 300),
            InventoryIntervalSeconds = (uint)Math.Clamp(policy.InventoryIntervalSeconds, 900, 7 * 86400)
        };

        if (effectiveTier == EndpointTier.Managed)
        {
            var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken);
            foreach (var check in checks)
            {
                var spec = new CheckSpec
                {
                    Id = check.Id.ToString("D"),
                    Type = ToProto(check.Type),
                    IntervalSeconds = (uint)Math.Clamp(check.IntervalSeconds, CheckParameters.MinimumIntervalSeconds, CheckParameters.MaximumIntervalSeconds)
                };
                foreach (var (key, value) in CheckParameters.Parse(check.Definition.ParametersJson).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    spec.Parameters[key] = value;
                }

                config.Checks.Add(spec);
            }
        }

        return new Built(config, ContentHash(config), endpoint);
    }

    /// <summary>Hash of the configuration with version and issue time cleared. Deterministic protobuf serialization.</summary>
    public static string ContentHash(AgentConfig config)
    {
        var copy = config.Clone();
        copy.Version = 0;
        copy.IssuedAt = null;
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            copy.WriteTo(output);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static ProtoCheckType ToProto(Core.Entities.CheckType type) => type switch
    {
        Core.Entities.CheckType.CpuUsage => ProtoCheckType.CpuUsage,
        Core.Entities.CheckType.MemoryUsage => ProtoCheckType.MemoryUsage,
        Core.Entities.CheckType.DiskFree => ProtoCheckType.DiskFree,
        Core.Entities.CheckType.ServiceRunning => ProtoCheckType.ServiceRunning,
        Core.Entities.CheckType.Uptime => ProtoCheckType.Uptime,
        _ => ProtoCheckType.Unspecified
    };
}
