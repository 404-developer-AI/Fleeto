using Fleeto.Core.Domain;
using Fleeto.Gateway.Data;
using Fleeto.Protocol.Agent.V1;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// The distribution and package manager state of an inventory (0.8.0, VULNERABILITIES.md §6) as the gateway stores it: cleaned and
/// bounded, the enabled module streams of an AlmaLinux endpoint included, and null for an agent that did not report it.
/// </summary>
public sealed class PackageInfoTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_inventory_without_package_info_stores_none()
    {
        Assert.Null(GatewayStore.PackageInfoJson(new Inventory(), Now));
    }

    [Fact]
    public void The_module_streams_of_an_almalinux_endpoint_are_stored_cleaned_and_without_duplicates()
    {
        var info = new PackageInfo { DistributionId = "almalinux", DistributionVersion = "8.5", PackageManager = "rpm", RestartRequired = true };
        info.ModuleStreams.Add(["container-tools:rhel8", "virt:rhel", "virt:rhel", "perl:5.26\0", " "]);

        var state = PackageInventory.Parse(GatewayStore.PackageInfoJson(new Inventory { Packages = info }, Now));

        Assert.NotNull(state);
        Assert.Equal(("almalinux", "8.5", "rpm", true), (state.DistributionId, state.DistributionVersion, state.PackageManager, state.RestartRequired));
        Assert.Equal(["container-tools:rhel8", "virt:rhel", "perl:5.26"], state.ModuleStreams);
    }

    [Fact]
    public void A_dpkg_endpoint_has_no_module_streams()
    {
        var info = new PackageInfo { DistributionId = "ubuntu", DistributionVersion = "24.04", PackageManager = "dpkg", PackageLists = "ok" };

        var state = PackageInventory.Parse(GatewayStore.PackageInfoJson(new Inventory { Packages = info }, Now));

        Assert.NotNull(state);
        Assert.Null(state.ModuleStreams);
        Assert.Equal("ok", state.PackageLists);
    }
}
