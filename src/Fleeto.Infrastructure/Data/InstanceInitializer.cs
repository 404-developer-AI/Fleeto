using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Infrastructure.Data;

/// <param name="ApplyGrants">False only in tests whose database has no application roles.</param>
public sealed record InstanceInitOptions(string Fqdn, string AgentHostName, int AgentPort, string WebBaseUrl, bool ApplyGrants = true);

public sealed record InstanceInitResult(Guid InstanceId, string? SetupLink);

/// <summary>
/// Runs as the migrator after migrations: applies grants, creates the instance identity, data keys, roles, the
/// default policy and a starter monitoring template, and issues a first-admin setup link while no admin exists.
/// Idempotent: safe on every install and update.
/// </summary>
public sealed class InstanceInitializer
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly RootKey _rootKey;
    private readonly TimeProvider _time;
    private readonly ILogger<InstanceInitializer> _logger;

    public InstanceInitializer(IFleetoDbContextFactory dbFactory, RootKey rootKey, TimeProvider time, ILogger<InstanceInitializer> logger)
    {
        _dbFactory = dbFactory;
        _rootKey = rootKey;
        _time = time;
        _logger = logger;
    }

    public async Task<InstanceInitResult> RunAsync(InstanceInitOptions options, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;

        if (options.ApplyGrants)
        {
            _logger.LogInformation("Applying database grants");
            await db.Database.ExecuteSqlRawAsync(DatabaseGrants.BuildSql(), cancellationToken);
        }

        var instance = await db.InstanceSettings.SingleOrDefaultAsync(cancellationToken);
        if (instance is null)
        {
            instance = new InstanceSettings { Id = 1, InstanceId = Guid.NewGuid(), CreatedAt = now };
            db.InstanceSettings.Add(instance);
            _logger.LogInformation("Created instance {InstanceId}", instance.InstanceId);
        }

        // The FQDN may change (re-install on a new name); the instance id never does.
        instance.Fqdn = options.Fqdn;
        instance.AgentHostName = options.AgentHostName;
        instance.AgentPort = options.AgentPort;
        instance.WebBaseUrl = options.WebBaseUrl.TrimEnd('/');

        // Data keys wrapped before the rename to Fleeto (0.2.1) get the current label; retired keys too, their data may still exist.
        foreach (var dataKey in await db.DataKeys.Where(k => k.RootKeyId == _rootKey.Id).ToListAsync(cancellationToken))
        {
            if (EnvelopeSecretProtector.UpgradeLegacyWrap(dataKey, _rootKey))
            {
                _logger.LogInformation("Rewrapped data key {DataKeyId} ({Purpose}) with the Fleeto label", dataKey.Id, dataKey.Purpose);
            }
        }

        foreach (var purpose in SecretPurposes.All)
        {
            var active = await db.DataKeys.Where(k => k.Purpose == purpose && k.RetiredAt == null)
                .Select(k => k.RootKeyId).FirstOrDefaultAsync(cancellationToken);
            if (active is null)
            {
                db.DataKeys.Add(EnvelopeSecretProtector.CreateDataKey(_rootKey, purpose, now));
                _logger.LogInformation("Created data key for {Purpose}", purpose);
            }
            else if (active != _rootKey.Id)
            {
                throw new InvalidOperationException(
                    $"The data keys of this instance are wrapped by root key {active}, but the loaded root key is {_rootKey.Id}. " +
                    "The root key file does not belong to this database.");
            }
        }

        foreach (var roleName in FleetoRoles.All)
        {
            var normalized = roleName.ToUpperInvariant();
            if (!await db.Roles.AnyAsync(r => r.NormalizedName == normalized, cancellationToken))
            {
                db.Roles.Add(new ApplicationRole(roleName) { Id = Guid.NewGuid(), NormalizedName = normalized, ConcurrencyStamp = Guid.NewGuid().ToString() });
            }
        }

        if (!await db.Policies.AnyAsync(p => p.IsDefault, cancellationToken))
        {
            db.Policies.Add(new Policy
            {
                Id = Guid.NewGuid(),
                Name = "Default policy",
                Description = "Applies to every site without a linked policy.",
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (!await db.MonitoringTemplates.AnyAsync(cancellationToken))
        {
            db.MonitoringTemplates.Add(StarterTemplate(now));
        }

        await db.SaveChangesAsync(cancellationToken);

        string? setupLink = null;
        var adminRoleId = await db.Roles.Where(r => r.NormalizedName == FleetoRoles.Admin.ToUpperInvariant()).Select(r => r.Id).SingleAsync(cancellationToken);
        if (!await db.UserRoles.AnyAsync(ur => ur.RoleId == adminRoleId, cancellationToken))
        {
            // A fresh link on every run while no admin exists; older links stop working.
            await db.SetupTokens.Where(t => t.UsedAt == null).ExecuteDeleteAsync(cancellationToken);
            var (token, id, hash) = OpaqueTokens.Create(OpaqueTokens.SetupPrefix);
            db.SetupTokens.Add(new SetupToken { Id = id, TokenHash = hash, CreatedAt = now, ExpiresAt = now.AddHours(24) });
            await db.SaveChangesAsync(cancellationToken);
            setupLink = $"{instance.WebBaseUrl}/setup?token={token}";
        }

        return new InstanceInitResult(instance.InstanceId, setupLink);
    }

    /// <summary>Creates the database roles and grants them login. Needs a superuser connection (setup-dev, install.sh).</summary>
    public static string BuildRoleSql(IReadOnlyDictionary<string, string> passwordsByRole, string databaseName)
    {
        var sql = new System.Text.StringBuilder();
        foreach (var (role, password) in passwordsByRole)
        {
            var quotedPassword = "'" + password.Replace("'", "''") + "'";
            sql.AppendLine($"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN
                    CREATE ROLE {role} LOGIN PASSWORD {quotedPassword};
                  ELSE
                    ALTER ROLE {role} LOGIN PASSWORD {quotedPassword};
                  END IF;
                END $$;
                """);
        }

        return sql.ToString();
    }

    private static MonitoringTemplate StarterTemplate(DateTime now)
    {
        var template = new MonitoringTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Basic health",
            Description = "CPU, memory, disk space and uptime. A starting point; copy it to make your own variant.",
            CreatedAt = now,
            UpdatedAt = now
        };

        CheckDefinition Check(string name, CheckType type, int interval, double? warning, double? critical, string parameters, int failures) => new()
        {
            Id = Guid.NewGuid(),
            MonitoringTemplateId = template.Id,
            Name = name,
            Type = type,
            IntervalSeconds = interval,
            WarningThreshold = warning,
            CriticalThreshold = critical,
            ParametersJson = parameters,
            FailuresBeforeAlert = failures,
            CreatedAt = now,
            UpdatedAt = now
        };

        template.Checks.Add(Check("CPU usage", CheckType.CpuUsage, 300, 85, 95, "{}", 3));
        template.Checks.Add(Check("Memory usage", CheckType.MemoryUsage, 300, 85, 95, "{}", 3));
        template.Checks.Add(Check("Free disk space", CheckType.DiskFree, 900, 15, 5, """{"drive":"*"}""", 1));
        template.Checks.Add(Check("Uptime", CheckType.Uptime, 3600, 30, 60, "{}", 1));
        return template;
    }

    internal static NpgsqlConnectionStringBuilder Describe(string connectionString) => new(connectionString) { Password = null };
}
