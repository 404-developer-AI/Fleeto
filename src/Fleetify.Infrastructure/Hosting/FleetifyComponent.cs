namespace Fleetify.Infrastructure.Hosting;

/// <summary>
/// The process being configured. Decides the database role, the secret files it may read and the keys it
/// loads: only web, workers and the tool mount the root key; only the signer and the tool mount the signer key.
/// </summary>
public enum FleetifyComponent
{
    Web,
    Gateway,
    Signer,
    Workers,
    /// <summary>fleetify-tool: migrations, grants and instance initialisation, run as the migrator role.</summary>
    Tool
}

public static class FleetifyComponentExtensions
{
    /// <summary>Default database role for the component, e.g. <c>fleetify_web</c>.</summary>
    public static string DatabaseRole(this FleetifyComponent component) => component switch
    {
        FleetifyComponent.Tool => DatabaseRoles.Migrator,
        _ => "fleetify_" + component.ToString().ToLowerInvariant()
    };

    /// <summary>Password file name in the secrets directory, e.g. <c>db-web.password</c>.</summary>
    public static string DatabasePasswordFile(this FleetifyComponent component) => component switch
    {
        FleetifyComponent.Tool => "db-migrator.password",
        _ => "db-" + component.ToString().ToLowerInvariant() + ".password"
    };

    public static bool UsesRootKey(this FleetifyComponent component) =>
        component is FleetifyComponent.Web or FleetifyComponent.Workers or FleetifyComponent.Tool;

    public static bool UsesSignerKey(this FleetifyComponent component) =>
        component is FleetifyComponent.Signer or FleetifyComponent.Tool;
}

/// <summary>PostgreSQL role names. One role per container with the minimum grants (see DatabaseGrants).</summary>
public static class DatabaseRoles
{
    public const string Migrator = "fleetify_migrator";
    public const string Web = "fleetify_web";
    public const string Gateway = "fleetify_gateway";
    public const string Signer = "fleetify_signer";
    public const string Workers = "fleetify_workers";

    /// <summary>Read-only role for pg_dump, member of pg_read_all_data. Used by the backup job.</summary>
    public const string Backup = "fleetify_backup";

    public static readonly IReadOnlyList<string> Application = [Web, Gateway, Signer, Workers];
}
