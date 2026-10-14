using System.Reflection;
using Fleeto.Core.Domain;
using Fleeto.Web.Components;
using Fleeto.Web.Components.Layout;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Fleeto.Web.Tests;

/// <summary>
/// There is no fallback authorization policy (it would also cover static assets and the framework endpoints), so an
/// omitted attribute on a new page would silently make it public. These tests catch that, and prove that every settings
/// page requires the permission the settings panel shows it for (0.6.0).
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

    private static IEnumerable<string> Routes(Type page) => page.GetCustomAttributes<RouteAttribute>().Select(r => r.Template);

    [Fact]
    public void Every_signed_in_page_requires_a_role_or_a_known_permission()
    {
        // Pages declare the viewer policy (any role) or the permission they need (0.6.0); nothing else, so a page cannot lean
        // on a cookie role again.
        var wrong = RoutablePages()
            .Select(t => (Page: t, Authorize: t.GetCustomAttribute<AuthorizeAttribute>()))
            .Where(p => p.Authorize is not null)
            .Where(p => p.Authorize is RequirePermissionAttribute required
                ? !Permissions.IsKnown(required.Permission)
                : p.Authorize!.Policy is not (null or FleetoPolicies.Viewer))
            .Select(p => p.Page.FullName!)
            .ToList();

        Assert.True(wrong.Count == 0, "Pages with an unknown policy or permission:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void Settings_pages_require_the_permission_the_settings_panel_shows_them_for()
    {
        // The settings panel and the page agree, so a page is never listed for a user it then refuses, nor reachable without
        // the permission the panel asks.
        var settingsPages = RoutablePages()
            .Where(t => Routes(t).Any(r => r.StartsWith("/settings", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(settingsPages.Count >= SettingsPages.All.Count, "The test found fewer settings pages than the panel lists.");
        foreach (var entry in SettingsPages.All)
        {
            var page = settingsPages.SingleOrDefault(t => Routes(t).Contains(entry.Href, StringComparer.OrdinalIgnoreCase));
            Assert.True(page is not null, $"The settings panel lists {entry.Href}, but no page has that route.");
            Assert.Equal(entry.Permission, (page!.GetCustomAttribute<AuthorizeAttribute>() as RequirePermissionAttribute)?.Permission);
        }

        var unlisted = settingsPages
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>() is not RequirePermissionAttribute)
            .Select(t => t.FullName!)
            .ToList();
        Assert.True(unlisted.Count == 0, "Settings pages without a permission:\n" + string.Join("\n", unlisted));
    }

    [Fact]
    public void Administration_pages_require_an_administration_permission()
    {
        var wrong = SettingsPages.All
            .Where(p => p.Administration != Permissions.Find(p.Permission)!.InstanceWide)
            .Select(p => p.Href)
            .ToList();

        Assert.True(wrong.Count == 0, "Settings pages in the wrong group of the panel:\n" + string.Join("\n", wrong));
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
