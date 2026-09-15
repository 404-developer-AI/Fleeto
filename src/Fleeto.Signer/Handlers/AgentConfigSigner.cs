using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Services;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProtoTier = Fleeto.Protocol.Agent.V1.Tier;

namespace Fleeto.Signer.Handlers;

public enum ConfigSignStatus
{
    /// <summary>The endpoint does not exist (deleted); nothing to sign.</summary>
    EndpointNotFound,
    /// <summary>The stored configuration already has this content; no new version.</summary>
    Unchanged,
    /// <summary>A new version was signed and stored.</summary>
    Issued,
    /// <summary>The computed configuration broke a tier rule; nothing was signed.</summary>
    TierViolation
}

public sealed record ConfigSignResult(ConfigSignStatus Status, long Version = 0);

/// <summary>
/// Computes, signs and stores an endpoint's agent configuration inside the caller's transaction. Shared by the
/// AgentConfig handler and enrollment (which issues version 1 in the same transaction as the endpoint).
/// </summary>
public sealed class AgentConfigSigner
{
    public const string TierViolationReason =
        "The computed configuration does not match the endpoint's tier, so it was not signed. See the signer log.";

    private readonly AgentConfigBuilder _builder;
    private readonly SignerKeyRing _keyRing;
    private readonly ILogger<AgentConfigSigner> _logger;

    public AgentConfigSigner(AgentConfigBuilder builder, SignerKeyRing keyRing, ILogger<AgentConfigSigner> logger)
    {
        _builder = builder;
        _keyRing = keyRing;
        _logger = logger;
    }

    public async Task<ConfigSignResult> SignAsync(SigningContext context, Guid endpointId, CancellationToken cancellationToken)
    {
        var db = context.Db;

        // Serializes config signing per endpoint across signer processes, so two requests cannot both compute the
        // same next version. NO KEY UPDATE does not block rows that merely reference the endpoint.
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Endpoints" WHERE "Id" = {endpointId} FOR NO KEY UPDATE""", cancellationToken);

        var built = await _builder.BuildAsync(db, endpointId, _keyRing.InstanceId, cancellationToken);
        if (built is null)
        {
            return new ConfigSignResult(ConfigSignStatus.EndpointNotFound);
        }

        var config = built.Config;

        // Tier enforcement layer 2 (the signer): whatever the builder computed, an agent-only configuration never
        // carries checks and an agent-only endpoint never gets a managed configuration.
        var violation = config.Tier switch
        {
            ProtoTier.Managed when built.Endpoint.Tier != EndpointTier.Managed => "managed configuration for an agent-only endpoint",
            ProtoTier.Managed => null,
            ProtoTier.AgentOnly when config.Checks.Count > 0 => "agent-only configuration with checks",
            ProtoTier.AgentOnly => null,
            _ => "configuration without a tier"
        };
        if (violation is null && (config.EndpointId != endpointId.ToString("D") || config.InstanceId != _keyRing.InstanceId.ToString("D")))
        {
            violation = "configuration for another endpoint or instance";
        }

        if (violation is not null)
        {
            _logger.LogCritical("Refused to sign a {Violation} (endpoint {EndpointId}, stored tier {Tier}). This indicates a bug or tampering",
                violation, endpointId, built.Endpoint.Tier);
            return new ConfigSignResult(ConfigSignStatus.TierViolation);
        }

        var existing = await db.EndpointConfigs.IgnoreQueryFilters().SingleOrDefaultAsync(c => c.EndpointId == endpointId, cancellationToken);
        if (existing is not null && existing.ContentHash == built.ContentHash && existing.KeyId == _keyRing.SigningKeyId)
        {
            return new ConfigSignResult(ConfigSignStatus.Unchanged, existing.Version);
        }

        var version = Math.Max(existing?.Version ?? 0, built.Endpoint.ConfigVersion) + 1;
        config.Version = (ulong)version;
        config.IssuedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(context.Now, DateTimeKind.Utc));

        var payload = SerializeDeterministic(config);
        var signature = _keyRing.Sign(SignatureContexts.AgentConfig, payload);

        if (existing is null)
        {
            db.EndpointConfigs.Add(new EndpointConfig
            {
                EndpointId = endpointId,
                ClientId = built.Endpoint.ClientId,
                Version = version,
                Payload = payload,
                Signature = signature,
                KeyId = _keyRing.SigningKeyId,
                ContentHash = built.ContentHash,
                CreatedAt = context.Now
            });
        }
        else
        {
            existing.Version = version;
            existing.Payload = payload;
            existing.Signature = signature;
            existing.KeyId = _keyRing.SigningKeyId;
            existing.ContentHash = built.ContentHash;
            existing.CreatedAt = context.Now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Endpoints.IgnoreQueryFilters()
            .Where(e => e.Id == endpointId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ConfigVersion, version), cancellationToken);

        _logger.LogInformation("Signed configuration version {Version} for endpoint {EndpointId} ({Tier}, {Checks} checks)",
            version, endpointId, config.Tier, config.Checks.Count);
        return new ConfigSignResult(ConfigSignStatus.Issued, version);
    }

    /// <summary>Deterministic protobuf serialization: the bytes that are signed and that the agent verifies as-is.</summary>
    public static byte[] SerializeDeterministic(IMessage message)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            message.WriteTo(output);
        }

        return stream.ToArray();
    }
}
