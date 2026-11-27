using Fleeto.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Fleeto.Web.Components.Shared;

/// <summary>
/// Base for a component below a page that shows times. The browser time zone becomes known only after the first render of a full
/// page load; until then times are in the server zone (UTC). A page re-renders when it changes (<see cref="FleetoPageBase"/>), but
/// Blazor skips a child whose parameters did not change, such as <see cref="TimeText"/> with only a <see cref="DateTime"/>, so a
/// component that formats times re-renders itself.
/// </summary>
public abstract class TimeAwareComponent : ComponentBase, IDisposable
{
    private bool _subscribed;

    [Inject] protected TimeDisplay Time { get; set; } = default!;

    // Subscribed here rather than in OnInitialized, which a component overrides without calling the base.
    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_subscribed)
        {
            Time.ZoneChanged += OnZoneChanged;
            _subscribed = true;
        }

        return base.SetParametersAsync(parameters);
    }

    private void OnZoneChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        if (_subscribed)
        {
            Time.ZoneChanged -= OnZoneChanged;
        }

        GC.SuppressFinalize(this);
    }
}
