using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Fleeto.Web.Performance;

/// <summary>
/// Measures what a user does on a page (0.7.0, PERFORMANCE.md §4.1). Every inbound message of the circuit (an event handler, a JS
/// interop callback, a navigation) is one interaction of the page it happened on, keyed by the page's route template, with its
/// database work; the time a dialog stays open in it is left out (<see cref="PerfDialogService"/>). Loading a page is measured by
/// <see cref="Components.Shared.FleetoPageBase"/>, because a navigation does not wait for the page's data. Circuits are counted as
/// they open, close, lose and regain their connection. One handler per circuit.
/// </summary>
public sealed class PerfCircuitHandler : CircuitHandler
{
    private static int _open;

    private readonly PerfRecorder _recorder;
    private readonly PageRoutes _routes;
    private readonly NavigationManager _navigation;

    public PerfCircuitHandler(PerfRecorder recorder, PageRoutes routes, NavigationManager navigation)
    {
        _recorder = recorder;
        _routes = routes;
        _navigation = navigation;
    }

    /// <summary>Circuits open in this process right now.</summary>
    public static int Open => Volatile.Read(ref _open);

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _open);
        _recorder.Count(PerfCollector.Interactions, PerfKind.Circuit, "opened");
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref _open);
        _recorder.Count(PerfCollector.Interactions, PerfKind.Circuit, "closed");
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _recorder.Count(PerfCollector.Interactions, PerfKind.Circuit, "connection-down");
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _recorder.Count(PerfCollector.Interactions, PerfKind.Circuit, "connection-up");
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context => _recorder.Collects(PerfCollector.Interactions) ? MeasureAsync(next, context) : next(context);

    private async Task MeasureAsync(Func<CircuitInboundActivityContext, Task> next, CircuitInboundActivityContext context)
    {
        var route = Route();
        var scope = PerfContext.Begin(SourceOf(route));
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            _recorder.Complete(PerfCollector.Interactions, PerfKind.Interaction, route, "event", scope, failed);
        }
    }

    private string Route()
    {
        try
        {
            return _routes.Match(_navigation.Uri);
        }
        catch (InvalidOperationException)
        {
            // The navigation manager is not initialized before the circuit has its first page.
            return RouteTemplates.Unmatched;
        }
    }

    /// <summary>The source database work of a page counts under; the monitor's own page counts under its own (spec §5).</summary>
    public static string SourceOf(string route) =>
        string.Equals(route, PerformanceRules.DashboardRoute, StringComparison.OrdinalIgnoreCase) ? PerfKeys.DashboardSource : PerfKeys.InteractionSource(route);
}
