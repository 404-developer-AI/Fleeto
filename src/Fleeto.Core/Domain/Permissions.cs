using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>One permission a role can hold (0.6.0).</summary>
/// <param name="Key">Stable key stored in the database; never renamed once released.</param>
/// <param name="Group">Heading the role editor shows it under.</param>
/// <param name="Requires">Permissions a role must hold as well, so a role cannot change what it cannot see.</param>
public sealed record PermissionInfo(string Key, string Group, string Label, string Description, IReadOnlyList<string> Requires)
{
    /// <summary>
    /// Administration of the instance as a whole. Such a permission counts only for a user whose groups give every client
    /// without exception, so a user limited to clients cannot use it to reach further (a group, an API key, a user).
    /// </summary>
    public bool InstanceWide => Group == Permissions.AdministrationGroup;
}

/// <summary>
/// What a role allows (0.6.0). The groups of a user decide where, the roles what. This catalog is the only list of
/// permissions: the role editor offers every one of them, and a permission added later is held by no custom role until an
/// admin gives it, because a role holds exactly the permissions stored for it. Keys are stored, so they never change.
/// </summary>
public static class Permissions
{
    public const string AlertsGroup = "Alerts";
    public const string ClientsGroup = "Clients and sites";
    public const string EndpointsGroup = "Endpoints";
    public const string EndpointTabsGroup = "Endpoint detail";
    public const string RemoteGroup = "Remote sessions";
    public const string ScriptsGroup = "Scripts";
    public const string TemplatesGroup = "Templates";
    public const string AdministrationGroup = "Administration";

    public const string AlertsView = "alerts.view";
    public const string AlertsAcknowledge = "alerts.acknowledge";
    public const string AlertsHold = "alerts.hold";
    public const string AlertsResolve = "alerts.resolve";

    public const string ClientsView = "clients.view";
    public const string ClientsCreate = "clients.create";
    public const string ClientsEdit = "clients.edit";
    public const string ClientsDelete = "clients.delete";
    public const string ClientsTags = "clients.tags";
    public const string SitesCreate = "sites.create";
    public const string SitesEdit = "sites.edit";
    public const string SitesDelete = "sites.delete";
    public const string LinksEdit = "links.edit";
    public const string MaintenanceManage = "maintenance.manage";
    public const string AccessView = "access.view";

    public const string EndpointsEnroll = "endpoints.enroll";
    public const string EndpointsEdit = "endpoints.edit";
    public const string EndpointsMove = "endpoints.move";
    public const string EndpointsTier = "endpoints.tier";
    public const string EndpointsRevoke = "endpoints.revoke";
    public const string EndpointsDelete = "endpoints.delete";
    public const string EndpointsRestart = "endpoints.restart";

    public const string ChecksView = "checks.view";
    public const string ChecksEdit = "checks.edit";
    public const string ChecksRun = "checks.run";
    public const string SoftwareView = "software.view";
    public const string NotesView = "notes.view";
    public const string NotesCreate = "notes.create";
    public const string NotesDelete = "notes.delete";
    public const string JobsView = "jobs.view";
    public const string JobsCancel = "jobs.cancel";
    public const string PatchesView = "patches.view";
    public const string PatchesSync = "patches.sync";
    public const string PatchesDeploy = "patches.deploy";
    public const string Action1AgentInstall = "action1agent.install";
    public const string Action1AgentRemove = "action1agent.remove";
    public const string StorageView = "storage.view";
    public const string StorageScan = "storage.scan";
    public const string HistoryView = "history.view";

    public const string RemoteControl = "remote.control";
    public const string RemoteBackground = "remote.background";

    public const string ScriptsView = "scripts.view";
    public const string ScriptsEdit = "scripts.edit";
    public const string ScriptsRun = "scripts.run";
    public const string ScriptsApprove = "scripts.approve";
    public const string TasksView = "tasks.view";
    public const string TasksEdit = "tasks.edit";
    public const string TasksRun = "tasks.run";

    public const string ClientTemplatesView = "clienttemplates.view";
    public const string ClientTemplatesEdit = "clienttemplates.edit";
    public const string MonitoringTemplatesView = "monitoringtemplates.view";
    public const string MonitoringTemplatesEdit = "monitoringtemplates.edit";
    public const string PoliciesView = "policies.view";
    public const string PoliciesEdit = "policies.edit";
    public const string PatchPoliciesView = "patchpolicies.view";
    public const string PatchPoliciesEdit = "patchpolicies.edit";

    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string GroupsManage = "groups.manage";
    public const string TagsManage = "tags.manage";
    public const string LicensingManage = "licensing.manage";
    public const string SignInManage = "signin.manage";
    public const string EmailManage = "email.manage";
    public const string NotificationsManage = "notifications.manage";
    public const string IntegrationsManage = "integrations.manage";
    public const string ApiKeysManage = "apikeys.manage";
    public const string AgentUpdatesManage = "agentupdates.manage";
    public const string AgentUninstallsManage = "agentuninstalls.manage";
    public const string BackupsManage = "backups.manage";
    public const string AuditView = "audit.view";
    public const string InstanceHealthView = "instancehealth.view";
    public const string PerformanceView = "performance.view";
    public const string PerformanceManage = "performance.manage";
    public const string InstanceView = "instance.view";

    /// <summary>Every permission, in the order the role editor shows them.</summary>
    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(AlertsView, AlertsGroup, "View alerts", "The Alerts page and the alerts of an endpoint.", [ClientsView]),
        new(AlertsAcknowledge, AlertsGroup, "Acknowledge alerts", "Acknowledge an open alert.", [AlertsView]),
        new(AlertsHold, AlertsGroup, "Hold alerts", "Put an alert on hold until a time, or end the hold.", [AlertsView]),
        new(AlertsResolve, AlertsGroup, "Resolve alerts", "Resolve an alert by hand.", [AlertsView]),

        new(ClientsView, ClientsGroup, "View clients and endpoints",
            "The Clients workspace: clients, sites, endpoints, their summary and a refresh of the inventory.", []),
        new(ClientsCreate, ClientsGroup, "Create clients", "Create a client, also from a client template.", [ClientsView]),
        new(ClientsEdit, ClientsGroup, "Edit clients", "Rename a client or detach it from its client template.", [ClientsView]),
        new(ClientsDelete, ClientsGroup, "Delete clients", "Delete a client with all of its data.", [ClientsView]),
        new(ClientsTags, ClientsGroup, "Tag clients", "Give a client tags; a new tag is created on first use.", [ClientsView]),
        new(SitesCreate, ClientsGroup, "Create sites", "Add a site to a client.", [ClientsView]),
        new(SitesEdit, ClientsGroup, "Edit sites", "Rename a site or change its description.", [ClientsView]),
        new(SitesDelete, ClientsGroup, "Delete sites", "Delete a site with its endpoints.", [ClientsView]),
        new(LinksEdit, ClientsGroup, "Link policies and templates",
            "Link a policy, patch policy and monitoring templates to a client, site or endpoint.", [ClientsView]),
        new(MaintenanceManage, ClientsGroup, "Maintenance mode", "Put a client, site or endpoint in maintenance, or end it.", [ClientsView]),
        new(AccessView, ClientsGroup, "See who has access", "Who has access to a client, site or endpoint.", [ClientsView]),

        new(EndpointsEnroll, EndpointsGroup, "Add endpoints", "Create and revoke enrollment tokens, and enroll an endpoint again.", [ClientsView]),
        new(EndpointsEdit, EndpointsGroup, "Change the endpoint class", "Override whether an endpoint is a server or a workstation.", [ClientsView]),
        new(EndpointsMove, EndpointsGroup, "Move endpoints", "Move an endpoint to another site of its client.", [ClientsView]),
        new(EndpointsTier, EndpointsGroup, "Switch the license tier", "Switch an endpoint to managed or agent-only.", [ClientsView]),
        new(EndpointsRevoke, EndpointsGroup, "Revoke agents", "Revoke the certificate of an agent.", [ClientsView]),
        new(EndpointsDelete, EndpointsGroup, "Delete endpoints", "Delete an endpoint, optionally uninstalling its agent.", [ClientsView]),
        new(EndpointsRestart, EndpointsGroup, "Restart endpoints",
            "Save a task with a restart step, and run such a task now. The endpoint restarts when the step's turn comes.", [ClientsView]),

        new(ChecksView, EndpointTabsGroup, "View checks", "The Checks tab and the history of a check.", [ClientsView]),
        new(ChecksEdit, EndpointTabsGroup, "Edit checks of an endpoint",
            "Checks of one endpoint, adjustments of template checks and the monitoring templates linked to an endpoint.", [ChecksView]),
        new(ChecksRun, EndpointTabsGroup, "Run checks now", "Run a check now, or reset it and run it.", [ChecksView]),
        new(SoftwareView, EndpointTabsGroup, "View software", "The Software tab.", [ClientsView]),
        new(NotesView, EndpointTabsGroup, "View notes", "The Notes tab.", [ClientsView]),
        new(NotesCreate, EndpointTabsGroup, "Write notes", "Add a note, and edit your own notes.", [NotesView]),
        new(NotesDelete, EndpointTabsGroup, "Delete notes", "Delete any note.", [NotesView]),
        new(JobsView, EndpointTabsGroup, "View jobs", "The Jobs tab and the output of a job.", [ClientsView]),
        new(JobsCancel, EndpointTabsGroup, "Cancel jobs", "Cancel a job that has not finished.", [JobsView]),
        new(PatchesView, EndpointTabsGroup, "View patches", "The Patches tab and the history of deployments.", [ClientsView]),
        new(PatchesSync, EndpointTabsGroup, "Sync with Action1", "Read the patch state of an endpoint from Action1 now.", [PatchesView]),
        new(PatchesDeploy, EndpointTabsGroup, "Deploy updates", "Deploy missing updates on one or more endpoints.", [PatchesView]),
        new(Action1AgentInstall, EndpointTabsGroup, "Install the Action1 agent", "Install the Action1 agent on an endpoint.", [PatchesView]),
        new(Action1AgentRemove, EndpointTabsGroup, "Remove the Action1 agent",
            "Remove the Action1 agent, also when an endpoint is deleted or switched to agent-only.", [PatchesView]),
        new(StorageView, EndpointTabsGroup, "View storage", "The Storage tab.", [ClientsView]),
        new(StorageScan, EndpointTabsGroup, "Scan storage now", "Ask an endpoint to scan its drives now.", [StorageView]),
        new(HistoryView, EndpointTabsGroup, "View history", "The History tab: alerts, connections and the audit entries of an endpoint.",
            [ClientsView]),

        new(RemoteControl, RemoteGroup, "Remote control", "Start or join a remote control session.", [ClientsView]),
        new(RemoteBackground, RemoteGroup, "Remote background",
            "Start a remote background session: terminal as SYSTEM or root, files, services and processes.", [ClientsView]),

        new(ScriptsView, ScriptsGroup, "View scripts", "The script library and the versions of a script.", []),
        new(ScriptsEdit, ScriptsGroup, "Edit scripts", "Create, edit and delete scripts, and save new versions.", [ScriptsView]),
        new(ScriptsRun, ScriptsGroup, "Run scripts", "Run a script on endpoints, also from the remote terminal.", [ScriptsView, JobsView]),
        new(ScriptsApprove, ScriptsGroup, "Approve scripts",
            "Approve a script version written by somebody else, and change whether a policy requires approval.", [ScriptsView]),
        new(TasksView, ScriptsGroup, "View tasks", "The tasks, where they are linked and the output of their runs.", [ScriptsView, JobsView]),
        new(TasksEdit, ScriptsGroup, "Edit tasks",
            "Create, edit, link and delete tasks. A task runs its script by itself on every endpoint it is linked to.", [TasksView]),
        new(TasksRun, ScriptsGroup, "Run tasks now", "Start a task by hand on the endpoints it is linked to.", [TasksView, ScriptsRun]),

        new(ClientTemplatesView, TemplatesGroup, "View client templates", "The client templates.", []),
        new(ClientTemplatesEdit, TemplatesGroup, "Edit client templates", "Create, edit, copy and delete client templates.", [ClientTemplatesView]),
        new(MonitoringTemplatesView, TemplatesGroup, "View monitoring templates", "The monitoring templates and their checks.", []),
        new(MonitoringTemplatesEdit, TemplatesGroup, "Edit monitoring templates", "Create, edit, copy and delete monitoring templates and their checks.",
            [MonitoringTemplatesView]),
        new(PoliciesView, TemplatesGroup, "View policies", "The policies.", []),
        new(PoliciesEdit, TemplatesGroup, "Edit policies", "Create, edit, copy and delete policies.", [PoliciesView]),
        new(PatchPoliciesView, TemplatesGroup, "View patch policies", "The patch policies.", []),
        new(PatchPoliciesEdit, TemplatesGroup, "Edit patch policies", "Create, edit, copy and delete patch policies.", [PatchPoliciesView]),

        new(UsersManage, AdministrationGroup, "Users", "Add, link, change and delete users, set passwords and reset two-factor authentication.", []),
        new(RolesManage, AdministrationGroup, "Roles", "Create, edit and delete roles.", []),
        new(GroupsManage, AdministrationGroup, "Groups", "Create, edit and delete groups of users.", []),
        new(TagsManage, AdministrationGroup, "Tags", "Rename, recolor and delete tags.", []),
        new(LicensingManage, AdministrationGroup, "Licensing", "View license usage and load a license.", []),
        new(SignInManage, AdministrationGroup, "Sign-in", "Configure sign-in with Microsoft Entra ID.", []),
        new(EmailManage, AdministrationGroup, "Email", "Configure email through Microsoft Graph or SMTP.", []),
        new(NotificationsManage, AdministrationGroup, "Notification channels", "Create, edit and delete notification channels.", []),
        new(IntegrationsManage, AdministrationGroup, "Integrations", "Configure Action1 and its mappings.", []),
        new(ApiKeysManage, AdministrationGroup, "API keys", "Create and revoke API keys.", []),
        new(AgentUpdatesManage, AdministrationGroup, "Agent updates", "Pause, resume and release agent updates.", []),
        new(AgentUninstallsManage, AdministrationGroup, "Agent uninstalls", "View and dismiss agent uninstalls.", []),
        new(BackupsManage, AdministrationGroup, "Backups", "Configure backups and start one.", []),
        new(AuditView, AdministrationGroup, "Audit log", "Search the audit log of the instance.", []),
        new(InstanceHealthView, AdministrationGroup, "Instance health", "Instance health and its diagnostics report.", []),
        new(PerformanceView, AdministrationGroup, "Performance", "The performance monitor of Fleeto itself and its export for Claude Code.", []),
        new(PerformanceManage, AdministrationGroup, "Manage performance monitoring",
            "Change the level, collectors, thresholds and retention of the performance monitor.", [PerformanceView]),
        new(InstanceView, AdministrationGroup, "Instance", "The instance identity: FQDN, CA and signing key.", []),
    ];

    private static readonly Dictionary<string, PermissionInfo> ByKey = All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    public static PermissionInfo? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>
    /// Why a set of permissions cannot be saved, or null: an unknown key, or a permission without one it requires. Checked by
    /// the server whatever the editor sent.
    /// </summary>
    public static string? Problem(IReadOnlyCollection<string> permissions)
    {
        if (permissions.FirstOrDefault(p => !IsKnown(p)) is { } unknown)
        {
            return $"The permission {unknown} does not exist. Refresh the page and try again.";
        }

        foreach (var key in permissions)
        {
            var info = ByKey[key];
            if (info.Requires.FirstOrDefault(r => !permissions.Contains(r)) is { } missing)
            {
                return $"\"{info.Label}\" needs \"{ByKey[missing].Label}\" as well.";
            }
        }

        return null;
    }
}

/// <summary>
/// The roles every instance has (0.6.0): admin, technician and read-only. They cannot be changed or deleted, only copied
/// into a custom role. Their permissions are defined here and written to the database on every start, so the signer reads
/// every role the same way. When a feature adds a permission, decide here whether technician or read-only gets it; a custom
/// role never gets it by itself.
/// </summary>
public static class BuiltInRoles
{
    public static readonly IReadOnlyList<string> ReadOnlyPermissions =
    [
        Permissions.AlertsView, Permissions.ClientsView, Permissions.ChecksView, Permissions.SoftwareView, Permissions.NotesView,
        Permissions.JobsView, Permissions.PatchesView, Permissions.StorageView, Permissions.HistoryView, Permissions.ScriptsView,
        Permissions.ClientTemplatesView, Permissions.MonitoringTemplatesView, Permissions.PoliciesView, Permissions.PatchPoliciesView,
        Permissions.TasksView,
    ];

    public static readonly IReadOnlyList<string> TechnicianPermissions =
    [
        .. ReadOnlyPermissions,
        Permissions.AlertsAcknowledge, Permissions.AlertsHold, Permissions.AlertsResolve,
        Permissions.ClientsCreate, Permissions.ClientsEdit, Permissions.ClientsDelete, Permissions.ClientsTags,
        Permissions.SitesCreate, Permissions.SitesEdit, Permissions.SitesDelete, Permissions.LinksEdit, Permissions.MaintenanceManage,
        Permissions.EndpointsEnroll, Permissions.EndpointsEdit, Permissions.EndpointsMove, Permissions.EndpointsTier,
        Permissions.EndpointsRevoke, Permissions.EndpointsDelete, Permissions.EndpointsRestart,
        Permissions.ChecksEdit, Permissions.ChecksRun, Permissions.NotesCreate, Permissions.JobsCancel,
        Permissions.PatchesSync, Permissions.PatchesDeploy, Permissions.Action1AgentInstall, Permissions.Action1AgentRemove,
        Permissions.StorageScan, Permissions.RemoteControl, Permissions.RemoteBackground,
        Permissions.ScriptsEdit, Permissions.ScriptsRun, Permissions.TasksEdit, Permissions.TasksRun,
        Permissions.ClientTemplatesEdit, Permissions.MonitoringTemplatesEdit, Permissions.PoliciesEdit, Permissions.PatchPoliciesEdit,
    ];

    public static readonly IReadOnlyList<string> AdminPermissions = [.. Permissions.All.Select(p => p.Key)];

    public static bool IsBuiltIn(string roleName) => FleetoRoles.All.Contains(roleName);

    /// <summary>The permissions of a built-in role, or null for a custom one.</summary>
    public static IReadOnlyList<string>? PermissionsOf(string roleName) => roleName switch
    {
        FleetoRoles.Admin => AdminPermissions,
        FleetoRoles.Technician => TechnicianPermissions,
        FleetoRoles.ReadOnly => ReadOnlyPermissions,
        _ => null
    };

    /// <summary>The name a built-in role is shown with.</summary>
    public static string DisplayName(string roleName) => roleName switch
    {
        FleetoRoles.Admin => "Admin",
        FleetoRoles.Technician => "Technician",
        FleetoRoles.ReadOnly => "Read-only",
        _ => roleName
    };

    public static string Description(string roleName) => roleName switch
    {
        FleetoRoles.Admin => "Everything on every client, including Settings. Groups do not apply.",
        FleetoRoles.Technician => "Manage the clients, sites and endpoints of their groups, templates, scripts and alerts.",
        FleetoRoles.ReadOnly => "View what their groups give, outside Settings.",
        _ => string.Empty
    };
}

/// <summary>Limits of a custom role (0.6.0).</summary>
public static class RoleRules
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;
    public const int MaxPermissionLength = 100;
}
