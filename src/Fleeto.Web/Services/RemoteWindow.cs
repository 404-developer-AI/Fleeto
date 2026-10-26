using Microsoft.JSInterop;
using MudBlazor;

namespace Fleeto.Web.Services;

/// <summary>
/// Opens the popup windows of remote sessions (0.3.0). Remote control has one window per endpoint: opening it again brings it to the front.
/// Remote background opens a new window, with a session of its own, every time (0.7.0), up to the sessions one endpoint serves at a time.
/// </summary>
public sealed class RemoteWindow
{
    private readonly IJSRuntime _js;
    private readonly ISnackbar _snackbar;

    public RemoteWindow(IJSRuntime js, ISnackbar snackbar)
    {
        _js = js;
        _snackbar = snackbar;
    }

    public static string BackgroundUrl(Guid endpointId) => $"/remote/background/{endpointId:D}";

    public static string ControlUrl(Guid endpointId) => $"/remote/control/{endpointId:D}";

    /// <summary>A window name of its own for every Remote background window, so a second one never replaces or focuses the first.</summary>
    public static string BackgroundWindowName(Guid endpointId, Guid window) => $"fleeto-background-{endpointId:N}-{window:N}";

    /// <summary>Opens the Remote control window (0.3.0 step 3, Windows endpoints).</summary>
    public async Task OpenControlAsync(Guid endpointId)
    {
        var opened = await _js.InvokeAsync<bool>("fleeto.openWindow", ControlUrl(endpointId), "fleeto-control-" + endpointId.ToString("N"));
        if (!opened)
        {
            _snackbar.Add("The browser blocked the Remote control window. Allow pop-ups for this site, then try again.", Severity.Warning);
        }
    }

    /// <summary>Opens a new Remote background window, next to the ones already open for this endpoint.</summary>
    public async Task OpenBackgroundAsync(Guid endpointId)
    {
        var opened = await _js.InvokeAsync<bool>("fleeto.openWindow", BackgroundUrl(endpointId), BackgroundWindowName(endpointId, Guid.NewGuid()));
        if (!opened)
        {
            _snackbar.Add("The browser blocked the Remote background window. Allow pop-ups for this site, then try again.", Severity.Warning);
        }
    }
}
