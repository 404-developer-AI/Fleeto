namespace Fleeto.Infrastructure.Hosting;

/// <summary>
/// The process being configured. Decides the database role, the secret files it may read and the keys it
/// loads: only web, workers and the tool mount the root key; only the signer and the tool mount the signer key.
/// </summary>
public enum FleetoComponent
{
    Web,
    Gateway,
    Signer,
    Workers,
    /// <summary>fleeto-tool: migrations, grants and instance initialisation, run as the migrator role.</summary>
    Tool
}

public static class FleetoComponentExtensions
{
    /// <summary>Default database role for the component, e.g. <c>fleeto_web</c>.</summary>
    public static string DatabaseRole(this FleetoComponent component) => component switch
    {
        FleetoComponent.Tool => DatabaseRoles.Migrator,
        _ => "fleeto_" + component.ToString().ToLowerInvariant()
    };

    /// <summary>Password file name in the secrets directory, e.g. <c>db-web.password</c>.</summary>
    public static string DatabasePasswordFile(this FleetoComponent component) => component switch
    {
        FleetoComponent.Tool => "db-migrator.password",
        _ => "db-" + component.ToString().ToLowerInvariant() + ".password"
    };

    /// <summary>
    /// PostgreSQL of an instance accepts 100 connections, 3 of them kept for the superuser. The pools of the components that run together
    /// stay below that with room for the notification listener of each and the nightly backup, so a peak waits for a free connection
    /// instead of failing with "too many clients" (found by the relay load test of 0.3.0 step 7, decided 2026-09-19): the gateway carries
    /// the agents and the relay, web the technicians and the API, workers the background work, the signer one request at a time.
    /// </summary>
    public static int DefaultPoolSize(this FleetoComponent component) => component switch
    {
        FleetoComponent.Gateway => 40,
        FleetoComponent.Web => 30,
        FleetoComponent.Workers => 15,
        FleetoComponent.Signer => 5,
        _ => 5
    };

    public static bool UsesRootKey(this FleetoComponent component) =>
        component is FleetoComponent.Web or FleetoComponent.Workers or FleetoComponent.Tool;

    public static bool UsesSignerKey(this FleetoComponent component) =>
        component is FleetoComponent.Signer or FleetoComponent.Tool;
}

/// <summary>PostgreSQL role names. One role per container with the minimum grants (see DatabaseGrants).</summary>
public static class DatabaseRoles
{
    public const string Migrator = "fleeto_migrator";
    public const string Web = "fleeto_web";
    public const string Gateway = "fleeto_gateway";
    public const string Signer = "fleeto_signer";
    public const string Workers = "fleeto_workers";

    /// <summary>Read-only role for pg_dump, member of pg_read_all_data. Used by the backup job.</summary>
    public const string Backup = "fleeto_backup";

    public static readonly IReadOnlyList<string> Application = [Web, Gateway, Signer, Workers];
}
