using System.Reflection;
using Fleetify.Web.Components;
using Fleetify.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Fleetify.Web.Tests;

/// <summary>
/// There is no fallback authorization policy (it would also cover static assets and the framework endpoints), so an
/// omitted attribute on a new page would silently make it public. These tests catch that, and prove that every settings
/// page requires the admin policy, except the template pages listed by name.
/// </summary>
public class PageAuthorizationTests
{
    private static IReadOnlyList<Type> RoutablePages() =>
        typeof(App).Assembly.GetTypes().Where(t => t.GetCustomAttributes<RouteAttribute>().Any()).ToList();

    [Fact]
    public void Every_page_declares_whether_it_needs_a_signed_in_user()
    {
        var undeclared = RoutablePages()
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>() is null && t.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(undeclared.Count == 0, "Pages without [Authorize] or [AllowAnonymous]:\n" + string.Join("\n", undeclared));
    }

    /// <summary>
    /// Templates live in the settings workspace but are readable by every signed-in user; the services enforce who may
    /// change them. Every other settings page is admin-only, so a page that should not be must be added here on purpose.
    /// </summary>
    private static readonly string[] TemplateRoutes =
    [
        "/settings/client-templates",
        "/settings/monitoring-templates",
        "/settings/monitoring-templates/{TemplateId:guid}",
        "/settings/policies",
        "/settings/scripts",
        "/settings/scripts/{ScriptId:guid}",
    ];

    private static bool IsTemplatePage(Type page) =>
        page.GetCustomAttributes<RouteAttribute>().All(r => TemplateRoutes.Contains(r.Template, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Settings_pages_require_the_admin_policy()
    {
        var settingsPages = RoutablePages()
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any(r =>
                r.Template.StartsWith("/settings", StringComparison.OrdinalIgnoreCase) ||
                r.Template.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase)))
            .Where(t => !IsTemplatePage(t))
            .ToList();

        // Users, licensing, email, notification channels, backups, audit log, instance, and the setup backups step.
        Assert.True(settingsPages.Count >= 8, "The test found fewer settings pages than exist; it has stopped testing anything.");
        var notAdmin = settingsPages.Where(t => t.GetCustomAttribute<AuthorizeAttribute>()?.Policy != FleetifyPolicies.Admin)
            .Select(t => t.FullName!).ToList();
        Assert.True(notAdmin.Count == 0, "Settings pages without the admin policy:\n" + string.Join("\n", notAdmin));
    }

    [Fact]
    public void Template_pages_require_the_viewer_policy()
    {
        var templatePages = RoutablePages().Where(IsTemplatePage).ToList();

        Assert.Equal(TemplateRoutes.Length, templatePages.Sum(t => t.GetCustomAttributes<RouteAttribute>().Count()));
        var notViewer = templatePages.Where(t => t.GetCustomAttribute<AuthorizeAttribute>()?.Policy != FleetifyPolicies.Viewer)
            .Select(t => t.FullName!).ToList();
        Assert.True(notViewer.Count == 0, "Template pages without the viewer policy:\n" + string.Join("\n", notViewer));
    }

    [Fact]
    public void Anonymous_pages_are_only_the_sign_in_setup_and_error_pages()
    {
        var anonymous = RoutablePages()
            .Where(t => t.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => r.Template))
            .OrderBy(t => t)
            .ToList();

        Assert.Equal(["/account/login", "/account/two-factor", "/Error", "/not-found", "/setup"], anonymous.OrderBy(t => t).ToList());
    }

    [Fact]
    public void Pages_reachable_before_two_factor_setup_are_only_setup_sign_in_and_error_pages()
    {
        var allowed = RoutablePages()
            .Where(t => t.GetCustomAttribute<AllowWithoutTwoFactorAttribute>() is not null)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => r.Template))
            .OrderBy(t => t)
            .ToList();

        Assert.Equal(["/account/login", "/account/recovery-codes", "/account/setup-2fa", "/account/two-factor", "/Error", "/not-found", "/setup"],
            allowed.OrderBy(t => t).ToList());
    }
}
