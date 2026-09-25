using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Loads the checks of an endpoint from the database and resolves them with <see cref="EffectiveChecks"/>. The SQL twin
/// <see cref="AppliesSql"/> expresses the same rule for set-based statements; a test proves both agree.
/// </summary>
public static class EffectiveCheckResolver
{
    /// <summary>
    /// SQL condition that is true when check definition <c>d</c> applies to endpoint <c>e</c> (ignoring tier). Aliases:
    /// <c>d</c> = "CheckDefinitions", <c>e</c> = "Endpoints".
    /// </summary>
    public static readonly string AppliesSql = """
        (d."Enabled" AND
        """ + CheckCatalog.PlatformSql + """
         AND (
          (d."MonitoringTemplateId" IS NOT NULL
            AND (d."AppliesTo" = 'All' OR d."AppliesTo" = COALESCE(e."ClassOverride", e."DetectedClass"))
            AND (EXISTS (SELECT 1 FROM "SiteMonitoringTemplates" sl WHERE sl."SiteId" = e."SiteId" AND sl."MonitoringTemplateId" = d."MonitoringTemplateId")
                 OR EXISTS (SELECT 1 FROM "ClientMonitoringTemplates" cl WHERE cl."ClientId" = e."ClientId" AND cl."MonitoringTemplateId" = d."MonitoringTemplateId")
                 OR EXISTS (SELECT 1 FROM "EndpointMonitoringTemplates" el WHERE el."EndpointId" = e."Id" AND el."MonitoringTemplateId" = d."MonitoringTemplateId"))
            AND NOT EXISTS (SELECT 1 FROM "EndpointCheckOverrides" o WHERE o."EndpointId" = e."Id" AND o."CheckDefinitionId" = d."Id" AND o."Disabled"))
          OR d."EndpointId" = e."Id"))
        """;

    public static Task<IReadOnlyList<EffectiveCheck>> LoadAsync(FleetoDbContext db, Endpoint endpoint, bool includeDisabledOnEndpoint,
        CancellationToken cancellationToken) =>
        LoadAsync(db, endpoint.Id, endpoint.ClientId, endpoint.SiteId, endpoint.EffectiveClass, endpoint.OsPlatform, includeDisabledOnEndpoint,
            cancellationToken);

    public static async Task<IReadOnlyList<EffectiveCheck>> LoadAsync(FleetoDbContext db, Guid endpointId, Guid clientId, Guid siteId,
        EndpointClass endpointClass, string? osPlatform, bool includeDisabledOnEndpoint, CancellationToken cancellationToken)
    {
        // Monitoring templates add up over the levels (0.6.0): the client's, the site's and the endpoint's all apply.
        var clientTemplates = await db.ClientMonitoringTemplates.AsNoTracking()
            .Where(l => l.ClientId == clientId)
            .Select(l => new { l.MonitoringTemplateId, l.MonitoringTemplate!.Name })
            .ToListAsync(cancellationToken);
        var siteTemplates = await db.SiteMonitoringTemplates.AsNoTracking()
            .Where(l => l.SiteId == siteId)
            .Select(l => new { l.MonitoringTemplateId, l.MonitoringTemplate!.Name })
            .ToListAsync(cancellationToken);
        var endpointTemplates = await db.EndpointMonitoringTemplates.AsNoTracking()
            .Where(l => l.EndpointId == endpointId)
            .Select(l => new { l.MonitoringTemplateId, l.MonitoringTemplate!.Name })
            .ToListAsync(cancellationToken);

        var templateIds = clientTemplates.Select(t => t.MonitoringTemplateId)
            .Concat(siteTemplates.Select(t => t.MonitoringTemplateId))
            .Concat(endpointTemplates.Select(t => t.MonitoringTemplateId))
            .Distinct().ToList();
        var definitions = await db.CheckDefinitions.AsNoTracking()
            .Where(d => (d.MonitoringTemplateId != null && templateIds.Contains(d.MonitoringTemplateId.Value)) || d.EndpointId == endpointId)
            .ToListAsync(cancellationToken);
        var overrides = await db.EndpointCheckOverrides.AsNoTracking()
            .Where(o => o.EndpointId == endpointId)
            .ToDictionaryAsync(o => o.CheckDefinitionId, cancellationToken);

        var clientNames = clientTemplates.ToDictionary(t => t.MonitoringTemplateId, t => t.Name);
        var siteNames = siteTemplates.ToDictionary(t => t.MonitoringTemplateId, t => t.Name);
        var endpointNames = endpointTemplates.ToDictionary(t => t.MonitoringTemplateId, t => t.Name);
        var candidates = new List<CheckCandidate>();
        foreach (var definition in definitions)
        {
            if (definition.EndpointId == endpointId)
            {
                candidates.Add(new CheckCandidate(definition, CheckSource.Endpoint, null));
                continue;
            }

            if (definition.MonitoringTemplateId is not { } templateId)
            {
                continue;
            }

            if (clientNames.TryGetValue(templateId, out var clientName))
            {
                candidates.Add(new CheckCandidate(definition, CheckSource.ClientTemplate, clientName));
            }

            if (siteNames.TryGetValue(templateId, out var siteName))
            {
                candidates.Add(new CheckCandidate(definition, CheckSource.SiteTemplate, siteName));
            }

            if (endpointNames.TryGetValue(templateId, out var endpointName))
            {
                candidates.Add(new CheckCandidate(definition, CheckSource.EndpointTemplate, endpointName));
            }
        }

        return EffectiveChecks.Resolve(endpointClass, candidates, overrides, includeDisabledOnEndpoint, osPlatform);
    }
}
