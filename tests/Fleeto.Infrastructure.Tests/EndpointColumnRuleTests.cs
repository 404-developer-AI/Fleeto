using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Column layouts of the endpoint list and the description of an endpoint (0.8.0, decided 2026-11-04): the catalog of columns, what a
/// layout may hold, which columns a viewer sees, and the rule that picks the layout of a user.
/// </summary>
public class EndpointColumnRuleTests
{
    [Fact]
    public void Every_column_has_a_stable_unique_key_a_name_and_a_known_permission()
    {
        var keys = EndpointColumns.All.Select(c => c.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var column in EndpointColumns.All)
        {
            Assert.Matches("^[a-z_]+$", column.Key);
            Assert.False(string.IsNullOrWhiteSpace(column.Name) || string.IsNullOrWhiteSpace(column.Description), column.Key);
            Assert.True(column.Permission is null || Permissions.IsKnown(column.Permission), column.Key);
        }

        Assert.Equal([EndpointColumns.Hostname], EndpointColumns.All.Where(c => c.Required).Select(c => c.Key));
        Assert.Null(ColumnLayoutRules.Validate(EndpointColumns.Default).Problem);
        // The default keeps the list as it was and puts the description after the host name.
        Assert.Equal([EndpointColumns.Hostname, EndpointColumns.Description], EndpointColumns.Default.Take(2));
    }

    [Fact]
    public void A_layout_holds_known_columns_once_and_always_the_host_name()
    {
        Assert.NotNull(ColumnLayoutRules.Validate([]).Problem);
        Assert.NotNull(ColumnLayoutRules.Validate(null).Problem);
        Assert.NotNull(ColumnLayoutRules.Validate([EndpointColumns.Description]).Problem);
        Assert.NotNull(ColumnLayoutRules.Validate([EndpointColumns.Hostname, "not-a-column"]).Problem);
        Assert.NotNull(ColumnLayoutRules.Validate([EndpointColumns.Hostname, EndpointColumns.Tier, EndpointColumns.Tier]).Problem);

        var (columns, problem) = ColumnLayoutRules.Validate([EndpointColumns.Uptime, EndpointColumns.Hostname, EndpointColumns.IpAddress]);
        Assert.Null(problem);
        Assert.Equal([EndpointColumns.Uptime, EndpointColumns.Hostname, EndpointColumns.IpAddress], columns);
        Assert.NotNull(ColumnLayoutRules.NameProblem(" "));
        Assert.NotNull(ColumnLayoutRules.NameProblem(new string('x', ColumnLayoutRules.MaxNameLength + 1)));
    }

    [Fact]
    public void A_viewer_sees_the_columns_in_order_within_its_permissions_and_client_and_site_only_where_they_say_something()
    {
        string[] layout = [EndpointColumns.OpenAlerts, EndpointColumns.Client, EndpointColumns.Site, EndpointColumns.Patches, EndpointColumns.Memory,
            "from-a-newer-release"];

        var everything = EndpointColumns.Visible(layout, _ => true, allClients: true, oneSite: false).Select(c => c.Key);
        // The host name is added in front when a layout lost it; an unknown key is skipped.
        Assert.Equal([EndpointColumns.Hostname, EndpointColumns.OpenAlerts, EndpointColumns.Client, EndpointColumns.Site, EndpointColumns.Patches,
            EndpointColumns.Memory], everything);

        var noAlertsOrPatches = EndpointColumns.Visible(layout, p => p == Permissions.ClientsView, allClients: true, oneSite: false).Select(c => c.Key);
        Assert.DoesNotContain(EndpointColumns.OpenAlerts, noAlertsOrPatches);
        Assert.DoesNotContain(EndpointColumns.Patches, noAlertsOrPatches);

        var oneClient = EndpointColumns.Visible(layout, _ => true, allClients: false, oneSite: false).Select(c => c.Key);
        Assert.DoesNotContain(EndpointColumns.Client, oneClient);
        Assert.Contains(EndpointColumns.Site, oneClient);

        var oneSite = EndpointColumns.Visible(layout, _ => true, allClients: false, oneSite: true).Select(c => c.Key);
        Assert.DoesNotContain(EndpointColumns.Site, oneSite);
    }

    [Fact]
    public void A_user_gets_the_first_layout_in_order_linked_to_one_of_its_roles_or_the_default()
    {
        var technician = Guid.NewGuid();
        var readOnly = Guid.NewGuid();
        var first = new ColumnLayoutChoice(Guid.NewGuid(), 1, [readOnly]);
        var second = new ColumnLayoutChoice(Guid.NewGuid(), 2, [technician, readOnly]);
        ColumnLayoutChoice[] layouts = [second, first];

        Assert.Equal(first.Id, ColumnLayoutRules.Pick(layouts, [readOnly]));
        Assert.Equal(second.Id, ColumnLayoutRules.Pick(layouts, [technician]));
        Assert.Equal(first.Id, ColumnLayoutRules.Pick(layouts, [technician, readOnly]));
        Assert.Null(ColumnLayoutRules.Pick(layouts, [Guid.NewGuid()]));
        Assert.Null(ColumnLayoutRules.Pick([], [technician]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Domain controller  ", "Domain controller")]
    [InlineData("Rack 2\r\nfloor 1\tleft", "Rack 2 floor 1 left")]
    public void A_description_is_one_trimmed_line_or_nothing(string? input, string? expected)
    {
        var (value, problem) = EndpointDescriptionRules.Clean(input);
        Assert.Null(problem);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void A_description_has_at_most_its_maximum_length()
    {
        Assert.Null(EndpointDescriptionRules.Clean(new string('x', EndpointDescriptionRules.MaxLength)).Problem);
        Assert.NotNull(EndpointDescriptionRules.Clean(new string('x', EndpointDescriptionRules.MaxLength + 1)).Problem);
    }
}
