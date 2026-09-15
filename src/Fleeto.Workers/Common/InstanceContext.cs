using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Common;

/// <summary>Identity of the instance as needed for links and object keys.</summary>
public sealed record InstanceInfo(Guid InstanceId, string Fqdn, string WebBaseUrl)
{
    public string EndpointUrl(Guid endpointId) => $"{WebBaseUrl}/endpoints/{endpointId:D}";
    public string LicensingUrl => $"{WebBaseUrl}/settings/licensing";
    public string BackupsUrl => $"{WebBaseUrl}/settings/backups";
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

public static class EmailAddresses
{
    /// <summary>Splits a comma- or semicolon-separated recipient list into plausible, distinct addresses.</summary>
    public static IReadOnlyList<string> Split(string? recipients) =>
        (recipients ?? string.Empty)
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(IsPlausible)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>A cheap sanity check; MimeKit parses the address properly at delivery time.</summary>
    public static bool IsPlausible(string address) =>
        address.Length is > 2 and <= 320 && address.IndexOf('@') > 0 && address.IndexOf('@') < address.Length - 1 &&
        !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
