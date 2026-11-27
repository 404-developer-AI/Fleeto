using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The instance-wide choice of patch management (0.9.0, <c>PATCHING.md</c> §16): only the exact value Fleeto means Fleeto; converting a
/// patch policy keeps what both engines have, drops what only Action1 has and never widens what the policy installs while it stays on;
/// the way an Action1 agent is removed; and the migration that gives an instance with Action1 the choice Action1 and every other one Fleeto.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PatchManagementRuleTests
{
    private readonly TestDatabase _db;

    public PatchManagementRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Theory]
    [InlineData("Fleeto", PatchManager.Fleeto)]
    [InlineData("Action1", PatchManager.Action1)]
    [InlineData(null, PatchManager.Action1)]
    [InlineData("", PatchManager.Action1)]
    [InlineData("fleeto", PatchManager.Action1)]
    [InlineData("Something else", PatchManager.Action1)]
    public void Only_the_exact_value_Fleeto_means_Fleeto(string? value, PatchManager expected)
    {
        Assert.Equal(expected, PatchManagement.Parse(value));
        Assert.Equal(expected == PatchManager.Fleeto ? "Fleeto" : "Action1", PatchManagement.ToValue(expected));
    }

    private static PatchPolicy Policy(Action<PatchPolicy>? change = null)
    {
        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(), Name = "Monday", Engine = PatchEngine.Action1, WeekDays = WeekDays.Monday, StartMinute = 20 * 60,
            InstallDelayDays = 0, AutoReboot = true, RebootTimeoutMinutes = 30, RetryHours = 12, TurnOffWindowsUpdate = true
        };
        change?.Invoke(policy);
        return policy;
    }

    [Fact]
    public void Converting_keeps_what_both_engines_have_and_drops_what_only_Action1_has()
    {
        var policy = Policy(p =>
        {
            p.Scope = PatchUpdateScope.Filtered;
            p.UpdateSources = ["Applications"];
            p.ExcludedVendors = ["Adobe*"];
            p.UpdateTypes = ["Security Updates", "Feature Updates"];
            p.Severities = ["Critical"];
            p.ExcludedNames = ["*Preview*"];
            p.RequireApproval = true;
            p.AutoInstallAgent = true;
        });

        var conversion = PatchManagement.ConvertToFleeto(policy);

        Assert.Equal(PatchEngine.Fleeto, policy.Engine);
        Assert.False(conversion.Disabled);
        Assert.True(policy.Enabled);
        Assert.Equal(5, conversion.Dropped.Count);
        Assert.Empty(policy.UpdateSources);
        Assert.Empty(policy.ExcludedVendors);
        Assert.Equal(new[] { "Security Updates" }, policy.UpdateTypes);
        Assert.Equal(new[] { "Critical" }, policy.Severities);
        Assert.Equal(new[] { "*Preview*" }, policy.ExcludedNames);
        Assert.Equal(PatchUpdateScope.Filtered, policy.Scope);
        Assert.False(policy.RequireApproval);
        Assert.False(policy.AutoInstallAgent);
        Assert.True(policy.TurnOffWindowsUpdate);
        Assert.True(policy.AutoReboot);
        Assert.Equal((WeekDays.Monday, 20 * 60), (policy.WeekDays, policy.StartMinute));
        Assert.Null(PatchPolicyRules.Validate(policy));
    }

    [Fact]
    public void A_filter_of_only_sources_becomes_all_updates_and_one_of_only_feature_updates_is_switched_off()
    {
        var sources = Policy(p =>
        {
            p.Scope = PatchUpdateScope.Filtered;
            p.UpdateSources = ["Windows - Mandatory"];
        });
        var sourcesConversion = PatchManagement.ConvertToFleeto(sources);
        Assert.Equal((PatchUpdateScope.All, true, false), (sources.Scope, sources.Enabled, sourcesConversion.Disabled));
        Assert.Null(PatchPolicyRules.Validate(sources));

        // Fleeto installs no feature updates: "all updates" would install more than the policy did, so it is switched off.
        var features = Policy(p =>
        {
            p.Scope = PatchUpdateScope.Filtered;
            p.UpdateTypes = ["Feature Updates"];
        });
        var featuresConversion = PatchManagement.ConvertToFleeto(features);
        Assert.Equal((PatchUpdateScope.All, false, true), (features.Scope, features.Enabled, featuresConversion.Disabled));
        Assert.Null(PatchPolicyRules.Validate(features));
    }

    [Fact]
    public void A_policy_of_engine_Fleeto_is_not_changed()
    {
        var policy = Policy(p =>
        {
            p.Engine = PatchEngine.Fleeto;
            p.Scope = PatchUpdateScope.Filtered;
            p.UpdateTypes = ["Security Updates"];
        });

        var conversion = PatchManagement.ConvertToFleeto(policy);

        Assert.Empty(conversion.Dropped);
        Assert.Equal((PatchUpdateScope.Filtered, true), (policy.Scope, policy.Enabled));
    }

    [Theory]
    [InlineData(true, true, true, Action1RemovalWay.Action1)]
    [InlineData(true, false, false, Action1RemovalWay.Action1)]
    [InlineData(false, true, true, Action1RemovalWay.Job)]
    [InlineData(false, true, false, Action1RemovalWay.ByHand)]
    [InlineData(false, false, true, Action1RemovalWay.ByHand)]
    public void Action1_removes_what_it_knows_a_job_what_it_does_not_on_managed_Windows_and_the_rest_is_by_hand(bool action1Knows, bool managed,
        bool windows, Action1RemovalWay expected) =>
        Assert.Equal(expected, PatchManagement.RemovalWay(action1Knows, managed, windows));

    /// <summary>The statement of the migration <c>PatchManager</c>, run on its own inside a transaction that is rolled back.</summary>
    private static string MigrationSql() =>
        Assert.IsType<SqlOperation>(Assert.Single(new Migrations.PatchManager().UpOperations)).Sql;

    [Fact]
    public async Task The_migration_gives_an_instance_with_Action1_the_choice_Action1_and_any_other_Fleeto()
    {
        // The test database was migrated without an Action1 integration: a new instance patches with Fleeto.
        Assert.Equal(PatchManager.Fleeto, _db.MigratedPatchManager);

        await using var db = _db.DbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Settings.Where(s => s.Key == PatchManagement.SettingKey).ExecuteDeleteAsync();
        db.Integrations.Add(new Integration
        {
            Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, EncryptedCredentials = "x", CredentialName = "k",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(MigrationSql());
        Assert.Equal(PatchManager.Action1, await PatchManagers.GetAsync(db, default));

        // A choice that exists is left alone.
        await db.Settings.Where(s => s.Key == PatchManagement.SettingKey).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, "Fleeto"));
        await db.Database.ExecuteSqlRawAsync(MigrationSql());
        Assert.Equal(PatchManager.Fleeto, await PatchManagers.GetAsync(db, default));

        await transaction.RollbackAsync();
    }
}
