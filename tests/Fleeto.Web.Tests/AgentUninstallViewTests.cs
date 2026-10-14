using Fleeto.Core.Entities;
using Fleeto.Web.Services;

namespace Fleeto.Web.Tests;

/// <summary>
/// The list of agent uninstalls in Settings (0.6.0): a removed agent shows when the workers take it off the list, not the 30 days
/// Fleeto would have waited for it.
/// </summary>
public sealed class AgentUninstallViewTests
{
    private static readonly DateTime Deleted = new(2026, 10, 14, 0, 3, 0, DateTimeKind.Utc);

    private static AgentUninstallView View(DateTime? acceptedAt) =>
        new(Guid.NewGuid(), "000", "WS-MGT-01", "Admin", Deleted, Deleted + AgentUninstallRules.Lifetime, true, null, acceptedAt,
            acceptedAt is null ? null : AgentComponent.Agent);

    [Fact]
    public void A_waiting_uninstall_is_listed_until_it_expires()
    {
        Assert.Equal(Deleted + AgentUninstallRules.Lifetime, View(null).ListedUntil);
    }

    [Fact]
    public void A_removed_agent_is_listed_a_week_after_it_removed_itself()
    {
        var accepted = Deleted.AddMinutes(1);
        Assert.Equal(accepted + AgentUninstallRules.KeptAfterAccepted, View(accepted).ListedUntil);
    }

    [Fact]
    public void A_removal_just_before_the_expiry_is_listed_until_the_expiry()
    {
        var accepted = Deleted + AgentUninstallRules.Lifetime - TimeSpan.FromDays(1);
        Assert.Equal(Deleted + AgentUninstallRules.Lifetime, View(accepted).ListedUntil);
    }
}
