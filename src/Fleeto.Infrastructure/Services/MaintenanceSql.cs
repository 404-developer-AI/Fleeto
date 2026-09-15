namespace Fleeto.Infrastructure.Services;

/// <summary>
/// SQL twin of <see cref="Core.Domain.MaintenanceRules"/> for set-based statements in the workers. A test proves it agrees with the
/// C# rule and the EF Core predicate.
/// </summary>
public static class MaintenanceSql
{
    /// <summary>
    /// SQL condition that is true when endpoint <c>e</c> (alias of "Endpoints") is in effective maintenance at the timestamp parameter
    /// <c>@now</c>: its own, its site's or its client's maintenance is active, or a window occurrence of its policy (the site's linked
    /// policy, else the default policy) runs and applies to its class.
    /// </summary>
    public const string EndpointInMaintenance = """
        ((e."MaintenanceStartedAt" IS NOT NULL AND e."MaintenanceStartedAt" <= @now AND (e."MaintenanceEndsAt" IS NULL OR e."MaintenanceEndsAt" > @now))
         OR EXISTS (SELECT 1 FROM "Sites" ms WHERE ms."Id" = e."SiteId" AND ms."MaintenanceStartedAt" IS NOT NULL AND ms."MaintenanceStartedAt" <= @now
                    AND (ms."MaintenanceEndsAt" IS NULL OR ms."MaintenanceEndsAt" > @now))
         OR EXISTS (SELECT 1 FROM "Clients" mc WHERE mc."Id" = e."ClientId" AND mc."MaintenanceStartedAt" IS NOT NULL AND mc."MaintenanceStartedAt" <= @now
                    AND (mc."MaintenanceEndsAt" IS NULL OR mc."MaintenanceEndsAt" > @now))
         OR EXISTS (SELECT 1 FROM "MaintenanceWindowOccurrences" mo WHERE mo."StartsAt" <= @now AND mo."EndsAt" > @now
                    AND (mo."AppliesTo" = 'All' OR mo."AppliesTo" = COALESCE(e."ClassOverride", e."DetectedClass"))
                    AND mo."PolicyId" = COALESCE((SELECT sp."PolicyId" FROM "SitePolicies" sp WHERE sp."SiteId" = e."SiteId" LIMIT 1),
                                                 (SELECT mp."Id" FROM "Policies" mp WHERE mp."IsDefault" LIMIT 1))))
        """;
}
