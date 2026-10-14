using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace Fleeto.Web.Components;

/// <summary>
/// Shows the routed page once its authorization succeeds, like <see cref="AuthorizeRouteView"/>, but keeps the page that is
/// already shown while a navigation within the same page is checked again (0.6.0). The permission check reads the
/// database, so it completes asynchronously; <see cref="AuthorizeRouteView"/> then shows its placeholder in between, which
/// disposes the page and loses its state (the selected tab, search, filters) on every change of the query string, such as
/// opening an endpoint. Every navigation is still checked: a refusal replaces the page with the not-authorized content.
/// </summary>
public sealed class AuthorizedRouteView : ComponentBase
{
    [Parameter, EditorRequired] public RouteData RouteData { get; set; } = default!;
    [Parameter] public Type? DefaultLayout { get; set; }
    [Parameter] public RenderFragment<AuthenticationState>? NotAuthorized { get; set; }
    [Parameter] public RenderFragment? Authorizing { get; set; }

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    [Inject] private IAuthorizationPolicyProvider PolicyProvider { get; set; } = default!;
    [Inject] private IAuthorizationService AuthorizationService { get; set; } = default!;

    /// <summary>The page type the last completed check allowed, or null while nothing is allowed.</summary>
    private Type? _allowedPage;

    /// <summary>The state of the last completed check that refused the current page type.</summary>
    private AuthenticationState? _refused;

    private Type? _checkedPage;
    private int _check;

    protected override async Task OnParametersSetAsync()
    {
        if (AuthenticationState is null)
        {
            throw new InvalidOperationException($"{nameof(AuthorizedRouteView)} needs a cascading authentication state.");
        }

        var page = RouteData.PageType;
        if (page != _checkedPage)
        {
            // Another page: nothing of the previous one stays visible until this one is allowed.
            _allowedPage = null;
            _refused = null;
            _checkedPage = page;
        }

        var check = ++_check;
        var state = await AuthenticationState;
        var allowed = await IsAllowedAsync(page, state);
        if (check != _check)
        {
            // A newer navigation is being checked; its result decides.
            return;
        }

        _allowedPage = allowed ? page : null;
        _refused = allowed ? null : state;
    }

    private async Task<bool> IsAllowedAsync(Type page, AuthenticationState state)
    {
        if (page.IsDefined(typeof(IAllowAnonymous), inherit: true))
        {
            return true;
        }

        var authorizeData = page.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToArray();
        if (authorizeData.Length == 0)
        {
            return true;
        }

        var policy = await AuthorizationPolicy.CombineAsync(PolicyProvider, authorizeData)
            ?? throw new InvalidOperationException($"The page {page.FullName} has no authorization policy.");
        var result = await AuthorizationService.AuthorizeAsync(state.User, RouteData, policy);
        return result.Succeeded;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_allowedPage == RouteData.PageType)
        {
            builder.OpenComponent<RouteView>(0);
            builder.AddComponentParameter(1, nameof(RouteView.RouteData), RouteData);
            builder.AddComponentParameter(2, nameof(RouteView.DefaultLayout), DefaultLayout);
            builder.CloseComponent();
            return;
        }

        var content = _refused is { } refused ? NotAuthorized?.Invoke(refused) : Authorizing;
        builder.OpenComponent<LayoutView>(3);
        builder.AddComponentParameter(4, nameof(LayoutView.Layout), RouteData.PageType.GetCustomAttribute<LayoutAttribute>()?.LayoutType ?? DefaultLayout);
        builder.AddComponentParameter(5, nameof(LayoutView.ChildContent), content);
        builder.CloseComponent();
    }
}
