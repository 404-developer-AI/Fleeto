using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>The engine of one endpoint, as <see cref="PatchEngines.Query"/> resolves it.</summary>
public sealed class PatchEngineRow
{
    public Guid EndpointId { get; init; }
    public Guid ClientId { get; init; }
    public EndpointPatchEngine Engine { get; init; }
}

/// <summary>
/// The EF Core form of <see cref="PatchEngineRules"/> (0.9.0): the engine of every endpoint in the scope of the context. Compose
/// it like any query (join on <see cref="PatchEngineRow.EndpointId"/>); EF Core inlines it as a CASE with correlated subqueries,
/// the same as <see cref="PatchEngineRules.EngineSql"/>.
/// </summary>
public static class PatchEngines
{
    /// <param name="allowsManaged"><see cref="LicenseStatus.AllowsManaged"/> of the instance.</param>
    public static IQueryable<PatchEngineRow> Query(FleetoDbContext db, bool allowsManaged) =>
        db.Endpoints.Select(e => new PatchEngineRow
        {
            EndpointId = e.Id,
            ClientId = e.ClientId,
            Engine = e.Source != EndpointSource.Agent || e.Tier != EndpointTier.Managed || !allowsManaged
                ? EndpointPatchEngine.None
                : db.InventorySnapshots.Any(i => i.EndpointId == e.Id && i.Action1AgentId != "") ||
                  db.EndpointPatchStates.Any(s => s.EndpointId == e.Id && s.ExternalEndpointId != "")
                    ? EndpointPatchEngine.Action1
                    : e.OsPlatform == "windows" &&
                      (EF.Functions.ILike(e.OsName.Trim(), "%server%") || EF.Functions.ILike(e.OsName.Trim(), "windows%"))
                        ? EndpointPatchEngine.Fleeto
                        : e.OsPlatform == "linux" && e.OsName.Trim().StartsWith(RemoteSessionRules.ProxmoxOsPrefix)
                            ? EndpointPatchEngine.Fleeto
                            : e.OsPlatform == "linux" && e.OsName.Trim().StartsWith(RemoteSessionRules.SynologyOsPrefix)
                                ? EndpointPatchEngine.None
                                : e.OsPlatform == "linux" &&
                                  (EF.Functions.ILike(e.OsName.Trim(), "ubuntu%") || EF.Functions.ILike(e.OsName.Trim(), "debian%") ||
                                   EF.Functions.ILike(e.OsName.Trim(), "almalinux%"))
                                    ? EndpointPatchEngine.Fleeto
                                    : EndpointPatchEngine.None
        });
}
