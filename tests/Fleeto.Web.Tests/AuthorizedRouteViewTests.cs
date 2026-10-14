using System.Security.Claims;
using Bunit;
using Fleeto.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace Fleeto.Web.Tests;

/// <summary>
/// The route view of Fleeto (0.6.0): the permission check of a page completes asynchronously, and a navigation within the
/// same page (opening an endpoint changes only the query string) must keep the page and its state while it is checked again,
/// yet still replace it when the check refuses.
/// </summary>
public sealed class AuthorizedRouteViewTests : BunitContext
{
    private readonly PendingAuthorization _authorization = new();

    public AuthorizedRouteViewTests()
    {
        Services.AddAuthorizationCore(o => o.AddPolicy("test", p => p.RequireAssertion(_ => true)));
        Services.AddSingleton<IAuthorizationService>(_authorization);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tech")], "test"));
        Services.AddCascadingValue(_ => Task.FromResult(new AuthenticationState(user)));
    }

    private IRenderedComponent<AuthorizedRouteView> RenderView(Type page) =>
        Render<AuthorizedRouteView>(p => p
            .Add(v => v.RouteData, new RouteData(page, new Dictionary<string, object?>()))
            .Add(v => v.Authorizing, b => b.AddMarkupContent(0, "<p class=\"authorizing\">Checking</p>"))
            .Add(v => v.NotAuthorized, _ => b => b.AddMarkupContent(0, "<p class=\"refused\">Refused</p>")));

    private static void Navigate(IRenderedComponent<AuthorizedRouteView> view, Type page) =>
        view.Render(p => p.Add(v => v.RouteData, new RouteData(page, new Dictionary<string, object?>())));

    [Fact]
    public void A_navigation_within_the_same_page_keeps_the_page_while_it_is_checked_again()
    {
        var view = RenderView(typeof(ProtectedPage));
        view.Find(".authorizing");
        _authorization.Complete(true);
        var page = view.WaitForComponent<ProtectedPage>().Instance;

        Navigate(view, typeof(ProtectedPage));
        Assert.Same(page, view.FindComponent<ProtectedPage>().Instance);
        Assert.Empty(view.FindAll(".authorizing"));

        _authorization.Complete(true);
        view.WaitForAssertion(() => Assert.Same(page, view.FindComponent<ProtectedPage>().Instance));
    }

    [Fact]
    public void A_refusal_on_a_later_check_replaces_the_page()
    {
        var view = RenderView(typeof(ProtectedPage));
        _authorization.Complete(true);
        view.WaitForComponent<ProtectedPage>();

        Navigate(view, typeof(ProtectedPage));
        _authorization.Complete(false);

        view.WaitForElement(".refused");
        Assert.Empty(view.FindComponents<ProtectedPage>());
    }

    [Fact]
    public void Another_page_is_not_shown_before_its_check_succeeds()
    {
        var view = RenderView(typeof(ProtectedPage));
        _authorization.Complete(true);
        view.WaitForComponent<ProtectedPage>();

        Navigate(view, typeof(OtherProtectedPage));
        view.Find(".authorizing");
        Assert.Empty(view.FindComponents<ProtectedPage>());

        _authorization.Complete(true);
        view.WaitForComponent<OtherProtectedPage>();
    }

    [Authorize(Policy = "test")]
    private sealed class ProtectedPage : ComponentBase;

    [Authorize(Policy = "test")]
    private sealed class OtherProtectedPage : ComponentBase;

    /// <summary>An authorization service whose answer arrives when the test gives it, like the database check.</summary>
    private sealed class PendingAuthorization : IAuthorizationService
    {
        private TaskCompletionSource<AuthorizationResult>? _pending;

        public void Complete(bool allowed) =>
            _pending!.SetResult(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            _pending = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            AuthorizeAsync(user, resource, Array.Empty<IAuthorizationRequirement>());
    }
}
