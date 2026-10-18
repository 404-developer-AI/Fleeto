using System.Diagnostics;
using Fleeto.Infrastructure.Performance;
using MudBlazor;

namespace Fleeto.Web.Performance;

/// <summary>
/// The dialog service of MudBlazor, with the time a dialog stays open left out of the interaction that opened it (0.7.0). A click
/// handler that awaits a confirmation dialog would otherwise count the user's reading time as Fleeto being slow. Every way of
/// showing a dialog creates its reference here, so this one override covers them all.
/// </summary>
public sealed class PerfDialogService : DialogService
{
    public PerfDialogService(ILogger<DialogService> logger)
        : base(logger)
    {
    }

    public override IDialogReference CreateReference()
    {
        var reference = base.CreateReference();
        if (PerfContext.Current is { Ended: false } scope)
        {
            var opened = Stopwatch.GetTimestamp();
            _ = reference.Result.ContinueWith(_ => scope.AddUserWait(Stopwatch.GetElapsedTime(opened)), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return reference;
    }
}
