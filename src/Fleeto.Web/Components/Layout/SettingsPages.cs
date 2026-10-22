using Fleeto.Core.Domain;
using Fleeto.Web.Security;
using MudBlazor;

namespace Fleeto.Web.Components.Layout;

/// <summary>A page of the settings workspace and the permission it needs (0.6.0).</summary>
public sealed record SettingsPage(string Href, string Icon, string Label, string Permission, bool Administration);

/// <summary>The pages of the settings workspace in the order of its panel. A page shows only for a caller with its permission.</summary>
public static class SettingsPages
{
    public static readonly IReadOnlyList<SettingsPage> All =
    [
        new("/settings/client-templates", Icons.Material.Outlined.AccountTree, "Client templates", Permissions.ClientTemplatesView, false),
        new("/settings/monitoring-templates", Icons.Material.Outlined.Rule, "Monitoring templates", Permissions.MonitoringTemplatesView, false),
        new("/settings/policies", Icons.Material.Outlined.Tune, "Policies", Permissions.PoliciesView, false),
        new("/settings/patch-policies", Icons.Material.Outlined.SystemUpdateAlt, "Patch policies", Permissions.PatchPoliciesView, false),
        new("/settings/scripts", Icons.Material.Outlined.Terminal, "Scripts", Permissions.ScriptsView, false),
        new("/settings/tasks", Icons.Material.Outlined.EventRepeat, "Tasks", Permissions.TasksView, false),
        new("/settings/users", Icons.Material.Outlined.People, "Users", Permissions.UsersManage, true),
        new("/settings/roles", Icons.Material.Outlined.Badge, "Roles", Permissions.RolesManage, true),
        new("/settings/groups", Icons.Material.Outlined.Groups, "Groups", Permissions.GroupsManage, true),
        new("/settings/sign-in", Icons.Material.Outlined.Login, "Sign-in", Permissions.SignInManage, true),
        new("/settings/tags", Icons.Material.Outlined.Sell, "Tags", Permissions.TagsManage, true),
        new("/settings/licensing", Icons.Material.Outlined.WorkspacePremium, "Licensing", Permissions.LicensingManage, true),
        new("/settings/email", Icons.Material.Outlined.Email, "Email", Permissions.EmailManage, true),
        new("/settings/notification-channels", Icons.Material.Outlined.Campaign, "Notification channels", Permissions.NotificationsManage, true),
        new("/settings/integrations", Icons.Material.Outlined.Hub, "Integrations", Permissions.IntegrationsManage, true),
        new("/settings/api-keys", Icons.Material.Outlined.Key, "API keys", Permissions.ApiKeysManage, true),
        new("/settings/agent-updates", Icons.Material.Outlined.SystemUpdateAlt, "Agent updates", Permissions.AgentUpdatesManage, true),
        new("/settings/agent-uninstalls", Icons.Material.Outlined.DeleteSweep, "Agent uninstalls", Permissions.AgentUninstallsManage, true),
        new("/settings/backups", Icons.Material.Outlined.Backup, "Backups", Permissions.BackupsManage, true),
        new("/settings/audit-log", Icons.Material.Outlined.History, "Audit log", Permissions.AuditView, true),
        new("/settings/instance", Icons.Material.Outlined.Dns, "Instance", Permissions.InstanceView, true),
        new("/settings/instance-health", Icons.Material.Outlined.MonitorHeart, "Instance health", Permissions.InstanceHealthView, true),
        new("/settings/performance", Icons.Material.Outlined.Speed, "Performance", Permissions.PerformanceView, true),
        new("/settings/security", Icons.Material.Outlined.Security, "Security", Permissions.SecurityView, true),
    ];

    public static IReadOnlyList<SettingsPage> For(Caller? caller) => caller is null ? [] : All.Where(p => caller.Has(p.Permission)).ToList();
}
