using Microsoft.JSInterop;
using MudBlazor;

namespace Fleeto.Web.Services;

/// <summary>Opens the popup windows of remote sessions (0.3.0). One window per endpoint and kind: opening it again brings it to the front.</summary>
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

    /// <summary>Opens the Remote control window (0.3.0 step 3, Windows endpoints).</summary>
    public async Task OpenControlAsync(Guid endpointId)
    {
        var opened = await _js.InvokeAsync<bool>("fleeto.openWindow", ControlUrl(endpointId), "fleeto-control-" + endpointId.ToString("N"));
        if (!opened)
        {
            _snackbar.Add("The browser blocked the Remote control window. Allow pop-ups for this site, then try again.", Severity.Warning);
        }
    }

    public async Task OpenBackgroundAsync(Guid endpointId)
    {
        var opened = await _js.InvokeAsync<bool>("fleeto.openWindow", BackgroundUrl(endpointId), "fleeto-background-" + endpointId.ToString("N"));
        if (!opened)
        {
            _snackbar.Add("The browser blocked the Remote background window. Allow pop-ups for this site, then try again.", Severity.Warning);
        }
    }
}
