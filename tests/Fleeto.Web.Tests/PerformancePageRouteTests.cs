using Fleeto.Core.Performance;
using Fleeto.Web.Performance;
using MudBlazor;

namespace Fleeto.Web.Tests;

/// <summary>
/// Page interactions are keyed by the route template of their page, never the URL (0.7.0, PERFORMANCE.md §4.1): every page of web
/// is known, every URL of a page finds its own template, and only the HTTP routes outside the circuit are measured as requests.
/// </summary>
public sealed class PerformancePageRouteTests
{
    private static readonly PageRoutes Routes = new();

    [Fact]
    public void Every_page_template_matches_itself_with_sample_values()
    {
        Assert.Contains("/settings/performance", Routes.All);
        Assert.Contains("/clients", Routes.All);
        foreach (var template in Routes.All)
        {
            var url = string.Join('/', template.Split('/').Select(Sample));
            Assert.Equal(template, Routes.Match("https://rmm.example" + url));
        }
    }

    [Fact]
    public void A_url_never_becomes_a_key_of_its_own()
    {
        var id = Guid.NewGuid();
        var template = Routes.Match($"https://rmm.example/settings/tasks/{id}?tab=runs");
        Assert.DoesNotContain(id.ToString(), template, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RouteTemplates.Unmatched, Routes.Match("https://rmm.example/no/such/page/at/all"));
    }

    [Fact]
    public void The_monitor_page_counts_its_database_work_under_its_own_source()
    {
        Assert.Equal(PerfKeys.DashboardSource, PerfCircuitHandler.SourceOf(PerformanceRules.DashboardRoute));
        Assert.Equal("interaction:/alerts", PerfCircuitHandler.SourceOf("/alerts"));
    }

    [Theory]
    [InlineData("/api/v1/clients", true)]
    [InlineData("/api/account/login", true)]
    [InlineData("/api/account/entra/callback", true)]
    [InlineData("/account/login", true)]
    [InlineData("/agent/download/windows-amd64", true)]
    [InlineData("/_blazor", false)]
    [InlineData("/_framework/blazor.web.js", false)]
    [InlineData("/health", false)]
    [InlineData("/app.css", false)]
    [InlineData("/clients", false)]
    public void Only_requests_outside_the_circuit_are_measured_as_http(string path, bool measured)
    {
        Assert.Equal(measured, PerfHttpMiddleware.IsMeasured(path));
    }

    [Fact]
    public void The_dialog_service_overrides_where_every_dialog_is_created()
    {
        var method = typeof(PerfDialogService).GetMethod(nameof(DialogService.CreateReference))!;
        Assert.Equal(typeof(PerfDialogService), method.DeclaringType);
        Assert.True(typeof(IDialogService).IsAssignableFrom(typeof(PerfDialogService)));
    }

    private static string Sample(string segment)
    {
        if (!segment.StartsWith('{'))
        {
            return segment;
        }

        var inner = segment.Trim('{', '}', '*', '?');
        var constraint = inner.Contains(':') ? inner[(inner.IndexOf(':') + 1)..] : null;
        return constraint switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "int" or "long" => "42",
            "bool" => "true",
            _ => "sample"
        };
    }
}
