using System.Security.Cryptography;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using ProtoCheckType = Fleeto.Protocol.Agent.V1.CheckType;
using ProtoTier = Fleeto.Protocol.Agent.V1.Tier;

namespace Fleeto.Infrastructure.Services;

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
    public async Task<Built?> BuildAsync(FleetoDbContext db, Guid endpointId, Guid instanceId, CancellationToken cancellationToken = default)
    {
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var licenseStatus = await _licenses.GetStatusAsync(db, cancellationToken);
        var effectiveTier = TierRules.EffectiveTier(endpoint.Tier, licenseStatus);

        var policy = await EffectivePolicies.LoadAsync(db, endpoint.Id, cancellationToken) ?? new Policy();

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
            var scriptBytes = 0L;
            foreach (var check in checks)
            {
                var spec = new CheckSpec
                {
                    Id = check.Id.ToString("D"),
                    Type = ToProto(check.Type),
                    IntervalSeconds = (uint)Math.Clamp(check.IntervalSeconds, CheckParameters.MinimumIntervalSeconds, CheckParameters.MaximumIntervalSeconds)
                };
                var parameters = CheckParameters.Parse(check.Definition.ParametersJson);
                foreach (var (key, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    spec.Parameters[key] = value;
                }

                if (check.Type == Core.Entities.CheckType.Script)
                {
                    spec.IntervalSeconds = Math.Max(spec.IntervalSeconds, (uint)ScriptRules.MinCheckIntervalSeconds);
                    parameters.TryGetValue(CheckCatalog.ScriptParameter, out var scriptId);
                    var resolved = await ScriptCheckResolver.ResolveAsync(db, scriptId, endpoint.ClientId, endpoint.OsPlatform,
                        policy.ScriptApprovalRequired, cancellationToken);
                    var reason = resolved.UnavailableReason;
                    if (resolved.Version is { } version && reason is null)
                    {
                        var size = System.Text.Encoding.UTF8.GetByteCount(version.Body);
                        if (scriptBytes + size > ScriptRules.MaxCheckScriptBytesPerConfig)
                        {
                            reason = ScriptCheckResolver.TooLargeReason;
                        }
                        else
                        {
                            scriptBytes += size;
                            spec.Parameters["timeout_seconds"] = Math.Min(Math.Min(version.TimeoutSeconds, ScriptRules.MaxCheckTimeoutSeconds),
                                (int)Math.Min(spec.IntervalSeconds, int.MaxValue)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            spec.Script = new ScriptJob
                            {
                                Language = ToProto(resolved.Script!.Language),
                                Name = resolved.Script.Name,
                                Version = (uint)version.Number,
                                Body = version.Body,
                                Sha256 = version.Sha256
                            };
                        }
                    }

                    if (reason is not null)
                    {
                        spec.Parameters["unavailable"] = reason;
                    }
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
        Core.Entities.CheckType.Ping => ProtoCheckType.Ping,
        Core.Entities.CheckType.TcpPort => ProtoCheckType.TcpPort,
        Core.Entities.CheckType.Http => ProtoCheckType.Http,
        Core.Entities.CheckType.ProcessRunning => ProtoCheckType.ProcessRunning,
        Core.Entities.CheckType.PendingReboot => ProtoCheckType.PendingReboot,
        Core.Entities.CheckType.File => ProtoCheckType.File,
        Core.Entities.CheckType.CertificateExpiry => ProtoCheckType.CertificateExpiry,
        Core.Entities.CheckType.EventLog => ProtoCheckType.EventLog,
        Core.Entities.CheckType.SecurityCenter => ProtoCheckType.SecurityCenter,
        Core.Entities.CheckType.Script => ProtoCheckType.Script,
        _ => ProtoCheckType.Unspecified
    };

    public static Protocol.Agent.V1.ScriptLanguage ToProto(Core.Entities.ScriptLanguage language) => language switch
    {
        Core.Entities.ScriptLanguage.PowerShell => Protocol.Agent.V1.ScriptLanguage.Powershell,
        Core.Entities.ScriptLanguage.Batch => Protocol.Agent.V1.ScriptLanguage.Batch,
        Core.Entities.ScriptLanguage.Shell => Protocol.Agent.V1.ScriptLanguage.Shell,
        _ => Protocol.Agent.V1.ScriptLanguage.Bash
    };
}
