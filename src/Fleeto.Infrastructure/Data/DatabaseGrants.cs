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
            // Permissions of roles (0.6.0): the signer checks that the user behind a job, a remote session token or an uninstall
            // still holds the permission it needs.
            ["RolePermissions"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            // Groups of users (0.6.0): admins manage them in web; the signer reads them to check that the user behind a job or
            // a remote session token may still reach the endpoint.
            ["UserGroups"] = Grants(web: ReadWrite, signer: Read),
            ["UserGroupMembers"] = Grants(web: ReadWrite, signer: Read),
            ["UserGroupRules"] = Grants(web: ReadWrite, signer: Read),

            ["Clients"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            // Tags on clients (0.6.0): only the UI and the public API read them.
            ["Tags"] = Grants(web: ReadWrite),
            ["ClientTags"] = Grants(web: ReadWrite),
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
            // Policies and monitoring templates on client and endpoint level (0.6.0) are read wherever the site links are.
            ["ClientPolicies"] = Grants(web: ReadWrite, gateway: Read, signer: Read, workers: Read),
            ["EndpointPolicies"] = Grants(web: ReadWrite, gateway: Read, signer: Read, workers: Read),
            ["ClientMonitoringTemplates"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            // Patch policies (0.6.0): web links them, the workers turn them into automations in the patch management product.
            // The signer reads them to check that the patch policy of an endpoint still asks for an automatic installation of
            // the product's agent before it signs one.
            ["PatchPolicies"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["ClientPatchPolicies"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["SitePatchPolicies"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["EndpointPatchPolicies"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            // Maintenance window occurrences: written by web when a policy is saved and by the workers every hour.
            ["MaintenanceWindowOccurrences"] = Grants(web: ReadWrite, workers: ReadWrite),
            ["EndpointMonitoringTemplates"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["EndpointCheckOverrides"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["ClientTemplates"] = Grants(web: ReadWrite, workers: Read),
            ["ClientTemplateSites"] = Grants(web: ReadWrite, workers: Read),
            ["ClientTemplateSiteMonitoringTemplates"] = Grants(web: ReadWrite, workers: Read),
            ["ClientTemplateMonitoringTemplates"] = Grants(web: ReadWrite, workers: Read),

            // The workers write the results of the checks Fleeto evaluates itself (0.6.0: missing updates from Action1).
            ["CheckResults"] = Grants(web: Read, gateway: "INSERT", workers: "SELECT, INSERT, DELETE"),
            // The signer clears the batch sequences of an endpoint that enrolls again: the new agent state counts from 1 (0.2.0).
            ["IngestBatches"] = Grants(gateway: "SELECT, INSERT", signer: "SELECT, DELETE", workers: "SELECT, DELETE"),
            ["CheckStates"] = Grants(web: Read, workers: ReadWrite),
            // Check history rollups: maintained by the workers with the evaluation, read by web.
            ["CheckResultsHourly"] = Grants(web: Read, workers: ReadWrite),
            ["CheckResultsDaily"] = Grants(web: Read, workers: ReadWrite),
            ["Alerts"] = Grants(web: "SELECT, UPDATE", workers: ReadWrite),
            // What users did with an alert and their comment (0.6.0): written by web, never changed; deleted with the alert.
            ["AlertActivities"] = Grants(web: "SELECT, INSERT"),
            // Periods of maintenance mode (0.6.0): written by the trigger on the maintenance columns, which runs as web (the only
            // one that changes them); the workers apply retention.
            ["MaintenanceRecords"] = Grants(web: "SELECT, INSERT, UPDATE", workers: "SELECT, DELETE"),
            ["EndpointEvents"] = Grants(web: Read, gateway: "INSERT", workers: "SELECT, UPDATE, DELETE"),
            // Web asks, the workers apply a reset, the gateway delivers; nobody else can change a request.
            ["CheckRunRequests"] = Grants(web: "SELECT, INSERT", gateway: "SELECT, UPDATE", workers: "SELECT, UPDATE, DELETE"),
            // Deleted with their endpoint through the foreign key cascade.
            ["Notes"] = Grants(web: ReadWrite),
            // Storage analysis (0.6.0): the gateway stores what the agent reports, web reads it, the workers evaluate folder growth
            // and apply retention. Web and the workers (for a Disk free check) ask for scans, the gateway delivers them.
            ["StorageScans"] = Grants(web: Read, gateway: "SELECT, INSERT", workers: "SELECT, DELETE"),
            ["StorageScanRequests"] = Grants(web: "SELECT, INSERT", gateway: "SELECT, UPDATE", workers: "SELECT, INSERT, DELETE"),

            // Scripts and jobs (0.2.0): web writes, the signer decides and signs, the gateway delivers and stores output, the
            // workers expire and clean up. Who may request a job signature is also enforced by TR_SigningRequests_Origin.
            // From 0.6.0 the workers also write the job that installs the Action1 agent when a patch policy asks for it, and
            // TR_Jobs_Origin lets them write no other job.
            ["Scripts"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["ScriptVersions"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["Jobs"] = Grants(web: "SELECT, INSERT, UPDATE", gateway: "SELECT, UPDATE", signer: "SELECT, UPDATE", workers: "SELECT, INSERT, UPDATE, DELETE"),
            ["JobOutputChunks"] = Grants(web: Read, gateway: "SELECT, INSERT", workers: "SELECT, DELETE"),
            // Tasks (0.6.0): web writes them and their links; the workers only read them to start runs, and the signer reads them
            // again before it signs a job of a run Fleeto started. Runs are written by the workers, and by web for Run now.
            ["AutomationTasks"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["AutomationTaskSteps"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["ClientAutomationTasks"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["SiteAutomationTasks"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["EndpointAutomationTasks"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["MonitoringTemplateAutomationTasks"] = Grants(web: ReadWrite, signer: Read, workers: Read),
            ["AutomationTaskStates"] = Grants(web: Read, workers: ReadWrite),
            ["AutomationTaskRuns"] = Grants(web: "SELECT, INSERT", signer: Read, workers: ReadWrite),
            ["AutomationTaskRunTargets"] = Grants(web: "SELECT, INSERT", signer: Read, workers: ReadWrite),

            // Remote sessions (0.3.0): web opens them, the signer signs each participant's token, the gateway pairs browser and endpoint
            // and records who joined and left, the workers end stale rows and apply retention. Actions are reported by the endpoint.
            ["RemoteSessions"] = Grants(web: "SELECT, INSERT", gateway: "SELECT, UPDATE", signer: Read, workers: "SELECT, UPDATE, DELETE"),
            ["RemoteSessionParticipants"] = Grants(web: "SELECT, INSERT, UPDATE", gateway: "SELECT, UPDATE", signer: "SELECT, UPDATE",
                workers: "SELECT, UPDATE, DELETE"),
            ["RemoteSessionActions"] = Grants(web: Read, gateway: "SELECT, INSERT", workers: "SELECT, DELETE"),

            // Who may request which kind is also enforced by trigger TR_SigningRequests_Origin (migration SigningRequestOrigin).
            // Web requests job signatures (0.2.0), the gateway certificates, the workers configurations.
            ["SigningRequests"] = Grants(web: "SELECT, INSERT", gateway: "SELECT, INSERT", signer: "SELECT, UPDATE", workers: "SELECT, INSERT, DELETE"),
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
            // Patch state (0.4.0): the workers read it from the patch management product, web and the public API show it.
            ["EndpointPatchStates"] = Grants(web: Read, workers: ReadWrite),
            ["EndpointMissingUpdates"] = Grants(web: Read, workers: ReadWrite),
            // Deployments (0.4.0 step 3): web writes what a technician asked for, the workers hand it to the product and
            // write back what it reports. Web never updates a deployment, so it cannot claim an outcome of its own.
            ["PatchDeployments"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),
            ["PatchDeploymentUpdates"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),
            ["PatchDeploymentTargets"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),
            // The product's history per endpoint (0.6.0): only the workers write what the product said.
            ["PatchDeploymentSteps"] = Grants(web: Read, workers: ReadWrite),
            // Requests about the patch management of an endpoint (0.6.0): web asks, only the workers can reach the product.
            ["PatchRequests"] = Grants(web: "SELECT, INSERT", workers: ReadWrite),
            // The Action1 agent of an endpoint and its automatic installation (0.6.0): only the workers judge it. The signer
            // reads it to refuse an automatic installation on an endpoint whose agent was removed through Fleeto.
            ["Action1AgentInstallStates"] = Grants(web: Read, signer: Read, workers: ReadWrite),
            // Uninstalling the agent of a deleted endpoint (0.6.0): web writes it with the deletion and removes it when an admin stops
            // waiting, the signer signs the command, the gateway delivers it and records the answer, the workers remove expired rows.
            ["AgentUninstalls"] = Grants(web: "SELECT, INSERT, DELETE", gateway: "SELECT, UPDATE", signer: "SELECT, UPDATE", workers: "SELECT, DELETE"),
            // Sign-in with Entra ID (0.5.0): web writes the authorization code and reads the outcome, the workers exchange it
            // and write the claims back. Web may delete its own row as soon as the sign-in is done.
            ["SignInExchanges"] = Grants(web: "SELECT, INSERT, DELETE", workers: ReadWrite),
            // Questions to Microsoft from Settings (0.5.0): web asks and reads the answer, the workers answer. Web never
            // updates a row, so it cannot write an answer of its own.
            ["MicrosoftRequests"] = Grants(web: "SELECT, INSERT, DELETE", workers: ReadWrite),
            // Integrations (0.4.0): an admin configures them in web; the workers poll and write back the status of the last attempt.
            ["Integrations"] = Grants(web: ReadWrite, workers: "SELECT, UPDATE"),
            // The workers keep the tenant name and the agent installer link of a mapping current (0.4.0); the signer reads
            // the link, because it composes the job that installs the product's agent and trusts no URL from web.
            // From 0.6.0 the workers also map the organization they created for a new client.
            ["IntegrationMappings"] = Grants(web: ReadWrite, signer: Read, workers: "SELECT, INSERT, UPDATE"),
            // Following clients and sites in the product (0.6.0): web records what has to happen in the same transaction as
            // the change in Fleeto, the workers make the calls. Web deletes an operation an admin dismisses; a site group is
            // removed by web only through the cascade of its site.
            ["IntegrationSiteGroups"] = Grants(web: Read, workers: ReadWrite),
            ["IntegrationOperations"] = Grants(web: "SELECT, INSERT, DELETE", workers: ReadWrite),
            // The automations of patch policies (0.6.0): only the workers write what they made in the product; web shows it.
            ["IntegrationAutomations"] = Grants(web: Read, workers: ReadWrite),
            ["BackupRuns"] = Grants(web: Read, workers: ReadWrite),
            ["InstanceHealthSamples"] = Grants(web: Read, workers: ReadWrite),
            ["InstanceHealthIssues"] = Grants(web: Read, workers: ReadWrite),
            // Web reads how far the performance rollup got (0.7.0), so a long period takes hours up to it and minutes after it.
            ["WorkerWatermarks"] = Grants(web: Read, workers: ReadWrite),

            // The performance monitor (0.7.0): every component writes its own minute rows (TR_PerfMinutes_Component and
            // TR_PerfRuntimeMinutes_Component refuse another component's), web reads them for the page, the workers roll them up
            // into hours, read the VPS and the database statistics, and apply retention. Everyone reads the settings; web
            // changes them.
            ["PerfMinutes"] = Grants(web: "SELECT, INSERT", gateway: "INSERT", signer: "INSERT", workers: "SELECT, INSERT, DELETE"),
            ["PerfRuntimeMinutes"] = Grants(web: "SELECT, INSERT", gateway: "INSERT", signer: "INSERT", workers: "SELECT, INSERT, DELETE"),
            ["PerfEvents"] = Grants(web: "SELECT, INSERT", gateway: "INSERT", signer: "INSERT", workers: "SELECT, INSERT, DELETE"),
            ["PerfHours"] = Grants(web: Read, workers: ReadWrite),
            ["PerfHostMinutes"] = Grants(web: Read, workers: "SELECT, INSERT, DELETE"),
            ["PerfStatementSnapshots"] = Grants(web: Read, workers: "SELECT, INSERT, DELETE"),
            ["PerfDatabaseSnapshots"] = Grants(web: Read, workers: "SELECT, INSERT, DELETE"),
            ["PerfTableSnapshots"] = Grants(web: Read, workers: "SELECT, INSERT, DELETE"),
            ["PerfMarkers"] = Grants(web: "SELECT, INSERT", workers: "SELECT, INSERT, DELETE"),
            // Statements measured in Diagnose (0.7.0 step 2): each component upserts its own (TR_PerfFingerprints_Component), web
            // reads them for the page and the export, the workers delete the ones no longer seen.
            ["PerfFingerprints"] = Grants(web: "SELECT, INSERT, UPDATE", gateway: "SELECT, INSERT, UPDATE", signer: "SELECT, INSERT, UPDATE",
                workers: "SELECT, INSERT, UPDATE, DELETE"),
            ["PerformanceSettings"] = Grants(web: "SELECT, INSERT, UPDATE", gateway: Read, signer: Read, workers: Read),
            // Edge checks and benchmarks (0.7.0 step 3): web writes the request of an admin, the workers run it, write the result
            // and delete old ones.
            ["PerfProbes"] = Grants(web: "SELECT, INSERT", workers: "SELECT, UPDATE, DELETE"),

            // The security monitor (0.7.0): web and the gateway write their own signals (TR_SecuritySignals_Component), the workers
            // read them and apply retention. Both read the blocked and allowed addresses; web writes what an admin adds or removes
            // (only manual blocks, TR_IpBlocks_Source), the workers the automatic blocks and their expiry. Web records the networks
            // users sign in from.
            ["SecuritySignals"] = Grants(web: "SELECT, INSERT", gateway: "INSERT", workers: "SELECT, DELETE"),
            ["IpBlocks"] = Grants(web: ReadWrite, gateway: Read, workers: ReadWrite),
            ["IpAllows"] = Grants(web: ReadWrite, gateway: Read, workers: Read),
            ["UserSignInNetworks"] = Grants(web: ReadWrite, workers: "SELECT, DELETE"),
            // Incidents (step 2): the workers open, update and resolve them; web acknowledges and resolves them by hand.
            ["SecurityIncidents"] = Grants(web: "SELECT, UPDATE", workers: ReadWrite),
            // Blocklists (step 3): an admin switches a list on or off and asks for a refresh in web; only the workers fetch them and
            // write their entries. Web and the gateway read the entries of the lists that are on.
            ["IpFeeds"] = Grants(web: "SELECT, UPDATE", gateway: Read, workers: ReadWrite),
            ["IpFeedEntries"] = Grants(web: Read, gateway: Read, workers: ReadWrite),

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
        // Tasks (0.6.0): the signer checks that an alert of one of a task's checks opened on the endpoint at the moment its run names;
        // titles and details, which name hosts and paths, stay unreadable to it.
        ("Alerts", DatabaseRoles.Signer, ["Id", "EndpointId", "Kind", "CheckDefinitionId", "OpenedAt"]),
        // The gateway reads what the exit codes of a script mean when a job ends (0.6.0); never the script's name or body.
        ("Scripts", DatabaseRoles.Gateway, ["Id", "SuccessExitCodes", "InfoExitCodes", "WarningExitCodes", "ErrorExitCodes"]),
        // INSERT ... RETURNING "Id" (EF Core) needs SELECT on the returned column; the rest of the audit trail stays unreadable.
        ("AuditEntries", DatabaseRoles.Gateway, ["Id"]),
        ("AuditEntries", DatabaseRoles.Signer, ["Id"]),
        // The security monitor (0.7.0) checks how API keys are set up; never their hash.
        ("ApiKeys", DatabaseRoles.Workers, ["Id", "Name", "CreatedAt", "ExpiresAt", "LastUsedAt", "RevokedAt"]),
        // The signer checks that the initiator and the approver of a job still exist, are not locked out and hold the right role;
        // never password hashes, security stamps or two-factor secrets. Whether a user is linked to Entra ID (0.5.0) tells it
        // that the second factor comes from Microsoft.
        ("AspNetUsers", DatabaseRoles.Signer, ["Id", "LockoutEnd", "TwoFactorEnabled", "EntraObjectId"]),
        ("AspNetUserRoles", DatabaseRoles.Signer, ["UserId", "RoleId"]),
        ("AspNetRoles", DatabaseRoles.Signer, ["Id", "NormalizedName"]),
    ];

    /// <summary>
    /// Functions an application role may call. Every one is created with EXECUTE revoked from PUBLIC, so only these roles can.
    /// The performance monitor (0.7.0 step 2): web runs ANALYZE on one table when an admin asks, and plans a stored statement.
    /// </summary>
    public static readonly IReadOnlyList<(string Function, string Role)> FunctionGrants =
    [
        ("fleeto_perf_analyze(text)", DatabaseRoles.Web),
        ("fleeto_perf_explain(text)", DatabaseRoles.Web),
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

        foreach (var (function, role) in FunctionGrants)
        {
            sql.AppendLine($"GRANT EXECUTE ON FUNCTION {function} TO {role};");
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
