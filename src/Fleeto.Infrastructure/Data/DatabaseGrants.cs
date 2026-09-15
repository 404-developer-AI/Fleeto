using System.Text;
using Fleeto.Infrastructure.Hosting;

namespace Fleeto.Infrastructure.Data;

/// <summary>
/// Least-privilege grants per container role (CLAUDE.md, Least privilege). Applied by <c>fleeto-tool migrate</c>
/// after every migration run: all privileges are revoked, then exactly these are granted. A test fails when a
/// table exists without an entry here, so a new table always gets a deliberate decision.
/// <para>
/// Column lists restrict access to key material: only the signer can read encrypted private keys.
/// Foreign key cascades run with the privileges of the table owner, so roles do not need DELETE on child tables
/// to delete a parent.
/// </para>
/// </summary>
public static class DatabaseGrants
{
    private const string Read = "SELECT";
    private const string ReadWrite = "SELECT, INSERT, UPDATE, DELETE";

    private static readonly string[] PublicSigningKeyColumns = ["Id", "PublicKey", "CreatedAt", "RetiredAt"];
    private static readonly string[] PublicCaColumns = ["Id", "CertificateDer", "Fingerprint", "CreatedAt", "ExpiresAt", "RetiredAt"];

    /// <summary>Table name → role → privileges. A missing role means no access.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            // Identity: web only; workers read admins for license emails.
            ["AspNetUsers"] = Grants(web: ReadWrite, workers: Read),
            ["AspNetRoles"] = Grants(web: ReadWrite, workers: Read),
            ["AspNetUserRoles"] = Grants(web: ReadWrite, workers: Read),
            ["AspNetUserClaims"] = Grants(web: ReadWrite),
            ["AspNetUserLogins"] = Grants(web: ReadWrite),
            ["AspNetUserTokens"] = Grants(web: ReadWrite),
            ["AspNetRoleClaims"] = Grants(web: ReadWrite),

            ["Clients"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["Sites"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["Endpoints"] = Grants(web: ReadWrite, gateway: "SELECT, UPDATE", signer: "SELECT, INSERT, UPDATE", workers: "SELECT, UPDATE"),
            // The signer revokes the earlier certificates of an endpoint that enrolls again (0.2.0).
            ["AgentCertificates"] = Grants(web: "SELECT, UPDATE", gateway: Read, signer: "SELECT, INSERT, UPDATE", workers: "SELECT, DELETE"),
            ["EnrollmentTokens"] = Grants(web: ReadWrite, gateway: Read, signer: "SELECT, UPDATE", workers: "SELECT, DELETE"),
            ["InventorySnapshots"] = Grants(web: Read, gateway: "SELECT, INSERT, UPDATE", workers: Read),

            // The gateway reads the update ring of the policy (0.2.1).
            ["Policies"] = Grants(web: ReadWrite, gateway: Read, signer: Read, workers: Read),
            ["MonitoringTemplates"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["CheckDefinitions"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["SiteMonitoringTemplates"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["SitePolicies"] = Grants(web: ReadWrite, gateway: Read, signer: Read, workers: Read),
            // Maintenance window occurrences: written by web when a policy is saved and by the workers every hour.
            ["MaintenanceWindowOccurrences"] = Grants(web: ReadWrite, workers: ReadWrite),
            ["EndpointMonitoringTemplates"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["EndpointCheckOverrides"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["ClientTemplates"] = Grants(web: ReadWrite, workers: Read),
            ["ClientTemplateSites"] = Grants(web: ReadWrite, workers: Read),
            ["ClientTemplateSiteMonitoringTemplates"] = Grants(web: ReadWrite, workers: Read),

            ["CheckResults"] = Grants(web: Read, gateway: "INSERT", workers: "SELECT, DELETE"),
            // The signer clears the batch sequences of an endpoint that enrolls again: the new agent state counts from 1 (0.2.0).
            ["IngestBatches"] = Grants(gateway: "SELECT, INSERT", signer: "SELECT, DELETE", workers: "SELECT, DELETE"),
            ["CheckStates"] = Grants(web: Read, workers: ReadWrite),
            // Check history rollups: maintained by the workers with the evaluation, read by web.
            ["CheckResultsHourly"] = Grants(web: Read, workers: ReadWrite),
            ["CheckResultsDaily"] = Grants(web: Read, workers: ReadWrite),
            ["Alerts"] = Grants(web: "SELECT, UPDATE", workers: ReadWrite),
            ["EndpointEvents"] = Grants(web: Read, gateway: "INSERT", workers: "SELECT, UPDATE, DELETE"),
            // Web asks, the workers apply a reset, the gateway delivers; nobody else can change a request.
            ["CheckRunRequests"] = Grants(web: "SELECT, INSERT", gateway: "SELECT, UPDATE", workers: "SELECT, UPDATE, DELETE"),
            // Deleted with their endpoint through the foreign key cascade.
            ["Notes"] = Grants(web: ReadWrite),

            // Scripts and jobs (0.2.0): web writes, the signer decides and signs, the gateway delivers and stores output, the
            // workers expire and clean up. Who may request a job signature is also enforced by TR_SigningRequests_Origin.
            ["Scripts"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["ScriptVersions"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["Jobs"] = Grants(web: "SELECT, INSERT, UPDATE", gateway: "SELECT, UPDATE", signer: "SELECT, UPDATE", workers: "SELECT, UPDATE, DELETE"),
            ["JobOutputChunks"] = Grants(web: Read, gateway: "SELECT, INSERT", workers: "SELECT, DELETE"),

            // Who may request which kind is also enforced by trigger TR_SigningRequests_Origin (migration SigningRequestOrigin).
            ["SigningRequests"] = Grants(web: Read, gateway: "SELECT, INSERT", signer: "SELECT, UPDATE", workers: "SELECT, INSERT, DELETE"),
            ["EndpointConfigs"] = Grants(web: Read, gateway: Read, signer: "SELECT, INSERT, UPDATE", workers: Read),
            ["ConfigChangeEvents"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),

            ["InstanceSettings"] = Grants(web: Read, gateway: Read, signer: Read, workers: Read),
            ["Settings"] = Grants(web: ReadWrite, signer: Read, workers: "SELECT, INSERT, UPDATE"),
            ["DataKeys"] = Grants(web: Read, workers: Read),
            ["Licenses"] = Grants(web: "SELECT, INSERT, UPDATE", signer: Read, workers: Read),
            ["SetupTokens"] = Grants(web: "SELECT, UPDATE"),
            // Public API keys (0.2.1): created, revoked and checked by web only. Never deleted, so revoked keys stay traceable.
            ["ApiKeys"] = Grants(web: "SELECT, INSERT, UPDATE"),
            ["ApiKeyClients"] = Grants(web: "SELECT, INSERT"),
            // Agent releases (0.2.1): the gateway records the release it loads and marks it current; web pauses or releases it to all.
            ["AgentReleases"] = Grants(web: "SELECT, UPDATE", gateway: "SELECT, INSERT, UPDATE", workers: Read),
            // Service and update state of agent and watchdog as the endpoint reports it; written by the gateway only.
            ["EndpointComponentStates"] = Grants(web: Read, gateway: "SELECT, INSERT, UPDATE", workers: Read),
            ["AuditEntries"] = Grants(web: "SELECT, INSERT", gateway: "INSERT", signer: "INSERT", workers: "SELECT, INSERT"),
            ["NotificationChannels"] = Grants(web: ReadWrite, workers: Read),
            ["OutboxEmails"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),
            ["NotificationChannelClients"] = Grants(web: ReadWrite, workers: Read),
            // Web queues test deliveries and reads the last delivery per channel; the workers deliver.
            ["OutboxWebhooks"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),
            ["BackupRuns"] = Grants(web: Read, workers: ReadWrite),
            ["WorkerWatermarks"] = Grants(workers: ReadWrite),

            // Key material: full access for the signer only; everyone else reads the public columns.
            ["InstanceSigningKeys"] = Grants(signer: "SELECT, INSERT, UPDATE"),
            ["CertificateAuthorities"] = Grants(signer: "SELECT, INSERT, UPDATE"),
        };

    /// <summary>Column-level SELECT grants on top of <see cref="Tables"/>.</summary>
    public static readonly IReadOnlyList<(string Table, string Role, string[] Columns)> ColumnGrants =
    [
        ("InstanceSigningKeys", DatabaseRoles.Web, PublicSigningKeyColumns),
        ("InstanceSigningKeys", DatabaseRoles.Gateway, PublicSigningKeyColumns),
        ("InstanceSigningKeys", DatabaseRoles.Workers, PublicSigningKeyColumns),
        ("CertificateAuthorities", DatabaseRoles.Web, PublicCaColumns),
        ("CertificateAuthorities", DatabaseRoles.Gateway, PublicCaColumns),
        ("CertificateAuthorities", DatabaseRoles.Workers, PublicCaColumns),
        // INSERT ... RETURNING "Id" (EF Core) needs SELECT on the returned column; the rest of the audit trail stays unreadable.
        ("AuditEntries", DatabaseRoles.Gateway, ["Id"]),
        ("AuditEntries", DatabaseRoles.Signer, ["Id"]),
        // The signer checks that the initiator and the approver of a job still exist, are not locked out and hold the right role;
        // never password hashes, security stamps or two-factor data.
        ("AspNetUsers", DatabaseRoles.Signer, ["Id", "LockoutEnd", "TwoFactorEnabled"]),
        ("AspNetUserRoles", DatabaseRoles.Signer, ["UserId", "RoleId"]),
        ("AspNetRoles", DatabaseRoles.Signer, ["Id", "NormalizedName"]),
    ];

    /// <summary>SQL that resets and applies all grants. Idempotent; run as the migrator (table owner).</summary>
    public static string BuildSql()
    {
        var sql = new StringBuilder();
        foreach (var role in DatabaseRoles.Application)
        {
            sql.AppendLine($"GRANT USAGE ON SCHEMA public TO {role};");
            sql.AppendLine($"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {role};");
            sql.AppendLine($"REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM {role};");
        }

        foreach (var (table, grants) in Tables)
        {
            foreach (var (role, privileges) in grants)
            {
                sql.AppendLine($"GRANT {privileges} ON TABLE \"{table}\" TO {role};");
                if (privileges.Contains("INSERT", StringComparison.Ordinal))
                {
                    // Identity columns need their sequence.
                    sql.AppendLine($"""
                        DO $$ DECLARE s text; BEGIN
                          FOR s IN SELECT pg_get_serial_sequence('"{table}"', a.attname)
                                   FROM pg_attribute a WHERE a.attrelid = '"{table}"'::regclass AND a.attnum > 0 AND NOT a.attisdropped
                          LOOP IF s IS NOT NULL THEN EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO {role}', s); END IF; END LOOP;
                        END $$;
                        """);
                }
            }
        }

        foreach (var (table, role, columns) in ColumnGrants)
        {
            sql.AppendLine($"GRANT SELECT ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) ON TABLE \"{table}\" TO {role};");
        }

        // The backup role (member of pg_read_all_data) is granted by a superuser in install.sh and setup-dev.ps1;
        // the migrator is not allowed to grant predefined roles.
        return sql.ToString();
    }

    private static IReadOnlyDictionary<string, string> Grants(string? web = null, string? gateway = null, string? signer = null, string? workers = null)
    {
        var grants = new Dictionary<string, string>();
        if (web is not null) grants[DatabaseRoles.Web] = web;
        if (gateway is not null) grants[DatabaseRoles.Gateway] = gateway;
        if (signer is not null) grants[DatabaseRoles.Signer] = signer;
        if (workers is not null) grants[DatabaseRoles.Workers] = workers;
        return grants;
    }
}
