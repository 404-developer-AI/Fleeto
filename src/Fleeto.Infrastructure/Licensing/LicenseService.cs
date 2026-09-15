using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Licensing;

public sealed record LicenseUsage(LicenseStatus Status, int ManagedInUse, string? CustomerName, string? Serial)
{
    public int Available => Math.Max(0, Status.Capacity - ManagedInUse);
}

public sealed record LicenseLoadResult(bool Success, string? Problem, LicenseDocument? Document)
{
    public static LicenseLoadResult Fail(string problem) => new(false, problem, null);
}

/// <summary>License status, usage and loading. Shared by web, workers and the signer.</summary>
public sealed class LicenseService
{
    /// <summary>Setting key holding the latest UTC time the instance has seen (clock rollback protection).</summary>
    public const string ClockSettingKey = "license.latest-seen-time";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ISecretProtector? _protector;
    private readonly IAuditLog? _audit;

    public LicenseService(IFleetoDbContextFactory dbFactory, TimeProvider time, ISecretProtector? protector = null, IAuditLog? audit = null)
    {
        _dbFactory = dbFactory;
        _time = time;
        _protector = protector;
        _audit = audit;
    }

    /// <summary>
    /// The protected clock: the later of the system clock and the latest time recorded in the database.
    /// Stops casual clock rollback; not a defence against restoring an old database (licensing is a commercial
    /// control, not a security boundary).
    /// </summary>
    public async Task<DateTime> ProtectedNowAsync(FleetoDbContext db, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var stored = await db.Settings.AsNoTracking()
            .Where(s => s.Key == ClockSettingKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        if (stored is not null && DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var latest) &&
            latest > now)
        {
            return latest;
        }

        return now;
    }

    /// <summary>Records the current time as the latest seen time. Called by the workers once a day and at start.</summary>
    public async Task AdvanceClockAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = await ProtectedNowAsync(db, cancellationToken);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == ClockSettingKey, cancellationToken);
        var value = now.ToString("O", CultureInfo.InvariantCulture);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = ClockSettingKey, Value = value, UpdatedAt = now });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LicenseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        return await GetStatusAsync(db, cancellationToken);
    }

    public async Task<LicenseStatus> GetStatusAsync(FleetoDbContext db, CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.IsActive)
            .Select(l => new { l.ManagedEndpointCount, l.ExpiresAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (license is null)
        {
            return LicenseStatus.None;
        }

        var now = await ProtectedNowAsync(db, cancellationToken);
        return LicenseStatus.Evaluate(license.ManagedEndpointCount, license.ExpiresAt, now);
    }

    public async Task<LicenseUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var status = await GetStatusAsync(db, cancellationToken);
        var inUse = await db.Endpoints.CountAsync(e => e.Tier == EndpointTier.Managed, cancellationToken);
        var license = await db.Licenses.AsNoTracking().Where(l => l.IsActive)
            .Select(l => new { l.CustomerName, l.Serial }).FirstOrDefaultAsync(cancellationToken);
        return new LicenseUsage(status, inUse, license?.CustomerName, license?.Serial);
    }

    /// <summary>
    /// Verifies and activates a license document. The previous license is deactivated, never deleted.
    /// Raises an instance-wide configuration change so every endpoint gets a configuration for its new effective tier.
    /// </summary>
    public async Task<LicenseLoadResult> LoadAsync(string text, Guid userId, string userName, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (_protector is null || _audit is null)
        {
            throw new InvalidOperationException("Loading a license requires the secret protector and the audit log.");
        }

        await using var db = _dbFactory.CreateSystem();
        var instance = await db.InstanceSettings.AsNoTracking().SingleAsync(cancellationToken);
        var verification = LicenseCodec.Verify(text, TrustedKeys.LicenseKeys, instance.Fqdn);
        if (!verification.IsValid || verification.Document is null)
        {
            return LicenseLoadResult.Fail(verification.Problem ?? "The license is not valid.");
        }

        var document = verification.Document;
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Licenses.Where(l => l.IsActive).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false), cancellationToken);

        var existing = await db.Licenses.SingleOrDefaultAsync(l => l.Serial == document.Serial, cancellationToken);
        var license = existing ?? new License { Id = Guid.NewGuid(), Serial = document.Serial };
        license.CustomerName = document.CustomerName;
        license.Fqdn = document.Fqdn;
        license.ManagedEndpointCount = document.ManagedEndpointCount;
        license.IssuedAt = DateTime.SpecifyKind(document.IssuedAt, DateTimeKind.Utc);
        license.ExpiresAt = DateTime.SpecifyKind(document.ExpiresAt, DateTimeKind.Utc);
        license.KeyId = verification.KeyId;
        license.EncryptedDocument = _protector.Protect(SecretPurposes.License, text, "Licenses|" + document.Serial);
        license.IsActive = true;
        license.LoadedAt = now;
        license.LoadedByUserId = userId;
        if (existing is null)
        {
            db.Licenses.Add(license);
        }

        db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Instance, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _audit.WriteAsync(new AuditRecord(AuditActions.LicenseLoaded, "License", document.Serial, null,
            AuditActorType.User, userId.ToString(), userName,
            new { document.Serial, document.CustomerName, document.ManagedEndpointCount, document.ExpiresAt }, ipAddress), cancellationToken);

        return new LicenseLoadResult(true, null, document);
    }
}
