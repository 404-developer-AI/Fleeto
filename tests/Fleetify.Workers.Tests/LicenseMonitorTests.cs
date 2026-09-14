using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Workers.Email;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Tests;

/// <summary>
/// Guarantees that a license transition that changes managed behaviour raises exactly one instance-wide configuration
/// change, that admins are emailed once for expiring soon and expired and daily during the grace period, and that a
/// license row that does not verify is reported rather than trusted silently.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class LicenseMonitorTests
{
    private readonly WorkersFixture _fixture;

    public LicenseMonitorTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<long> MaxInstanceEventIdAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.ConfigChangeEvents.Where(e => e.Scope == ConfigChangeScope.Instance).MaxAsync(e => (long?)e.Id) ?? 0;
    }

    private async Task<int> InstanceEventsAfterAsync(long id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.ConfigChangeEvents.CountAsync(e => e.Scope == ConfigChangeScope.Instance && e.Id > id);
    }

    private async Task<List<OutboxEmail>> LicenseEmailsAsync(string address)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.OutboxEmails.AsNoTracking().Where(e => e.ToAddress == address && e.Category == OutboxEmails.CategoryLicense)
            .OrderBy(e => e.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task License_transitions_raise_one_instance_config_change_and_email_admins()
    {
        var admin = await _fixture.CreateAdminAsync();
        var monitor = _fixture.LicenseMonitor();

        // Settle on a valid license first.
        await _fixture.Db.LoadTestLicenseAsync(1000);
        Assert.Equal(LicenseState.Valid, await monitor.EvaluateAsync(CancellationToken.None));
        var baseline = await MaxInstanceEventIdAsync();
        Assert.Equal(LicenseState.Valid, await monitor.EvaluateAsync(CancellationToken.None));
        Assert.Equal(0, await InstanceEventsAfterAsync(baseline));

        // Expiring soon: no change in behaviour, one email.
        await _fixture.Db.LoadTestLicenseAsync(1000, expiresAt: _fixture.Now.AddDays(7));
        Assert.Equal(LicenseState.ExpiringSoon, await monitor.EvaluateAsync(CancellationToken.None));
        await monitor.EvaluateAsync(CancellationToken.None);
        Assert.Equal(0, await InstanceEventsAfterAsync(baseline));
        Assert.Single(await LicenseEmailsAsync(admin.Email!));

        // Grace period ended: managed behaviour stops, exactly one instance change and one email.
        await _fixture.Db.LoadTestLicenseAsync(1000, expiresAt: _fixture.Now.AddDays(-20));
        Assert.Equal(LicenseState.Expired, await monitor.EvaluateAsync(CancellationToken.None));
        Assert.Equal(LicenseState.Expired, await monitor.EvaluateAsync(CancellationToken.None));
        Assert.Equal(1, await InstanceEventsAfterAsync(baseline));
        var emails = await LicenseEmailsAsync(admin.Email!);
        Assert.Equal(2, emails.Count);
        Assert.Contains("has expired", emails[^1].Subject);

        // Back to a valid license: managed behaviour returns, one more instance change.
        await _fixture.Db.LoadTestLicenseAsync(1000);
        Assert.Equal(LicenseState.Valid, await monitor.EvaluateAsync(CancellationToken.None));
        await monitor.EvaluateAsync(CancellationToken.None);
        Assert.Equal(2, await InstanceEventsAfterAsync(baseline));
    }

    [Fact]
    public async Task The_grace_period_email_goes_out_once_a_day()
    {
        var admin = await _fixture.CreateAdminAsync();
        var monitor = _fixture.LicenseMonitor();
        await _fixture.Db.LoadTestLicenseAsync(1000, expiresAt: _fixture.Now.AddDays(-1));
        try
        {
            Assert.Equal(LicenseState.GracePeriod, await monitor.EvaluateAsync(CancellationToken.None));
            await monitor.EvaluateAsync(CancellationToken.None);
            Assert.Single(await LicenseEmailsAsync(admin.Email!));

            _fixture.Db.Time.Advance(TimeSpan.FromDays(1));
            await monitor.EvaluateAsync(CancellationToken.None);
            await monitor.EvaluateAsync(CancellationToken.None);
            Assert.Equal(2, (await LicenseEmailsAsync(admin.Email!)).Count);
        }
        finally
        {
            await _fixture.Db.LoadTestLicenseAsync(1000);
            await monitor.EvaluateAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_license_row_that_cannot_be_decrypted_fails_verification()
    {
        // The test license row holds no real encrypted document.
        await _fixture.Db.LoadTestLicenseAsync(1000);
        Assert.False(await _fixture.LicenseMonitor().VerifyActiveLicenseAsync(CancellationToken.None));
    }
}
