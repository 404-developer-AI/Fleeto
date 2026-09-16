using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Data;

/// <summary>Identity of the instance as needed for links and object keys.</summary>
public sealed record InstanceInfo(Guid InstanceId, string Fqdn, string WebBaseUrl)
{
    public string EndpointUrl(Guid endpointId) => $"{WebBaseUrl}/endpoints/{endpointId:D}";
    public string LicensingUrl => $"{WebBaseUrl}/settings/licensing";
    public string BackupsUrl => $"{WebBaseUrl}/settings/backups";
    public string AuditLogUrl => $"{WebBaseUrl}/settings/audit-log";
}

/// <summary>Reads the instance identity and admin email addresses.</summary>
public static class InstanceQueries
{
    public static async Task<InstanceInfo> GetInstanceAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var instance = await db.InstanceSettings.AsNoTracking()
            .Select(i => new { i.InstanceId, i.Fqdn, i.WebBaseUrl })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The instance is not initialised. Run 'fleeto-tool migrate' for this instance.");

        var baseUrl = string.IsNullOrWhiteSpace(instance.WebBaseUrl) ? "https://" + instance.Fqdn : instance.WebBaseUrl.TrimEnd('/');
        return new InstanceInfo(instance.InstanceId, instance.Fqdn, baseUrl);
    }

    /// <summary>Email addresses of every user in the admin role.</summary>
    public static async Task<IReadOnlyList<string>> GetAdminEmailsAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var adminRole = FleetoRoles.Admin.ToUpperInvariant();
        var emails = await (
                from user in db.Users
                join userRole in db.UserRoles on user.Id equals userRole.UserId
                join role in db.Roles on userRole.RoleId equals role.Id
                where role.NormalizedName == adminRole && user.Email != null && user.Email != ""
                select user.Email!)
            .ToListAsync(cancellationToken);

        return emails.Select(e => e.Trim()).Where(EmailAddresses.IsPlausible).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
