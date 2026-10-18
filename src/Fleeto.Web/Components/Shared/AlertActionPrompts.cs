using Fleeto.Core.Entities;
using Fleeto.Web.Components.Dialogs;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using MudBlazor;

namespace Fleeto.Web.Components.Shared;

/// <summary>
/// The dialogs behind the actions on an alert (0.6.0): each asks the comment that is kept with the alert, required for acknowledge,
/// hold and resolve and optional for ending a hold. Shared by the alert list, the endpoint history and the checks of an endpoint.
/// </summary>
public static class AlertActionPrompts
{
    /// <summary>Runs a service call and reports its outcome; <see cref="FleetoPageBase"/> RunAsync.</summary>
    public delegate Task<bool> Runner(Func<Caller, Task<ServiceResult>> action, string? successMessage);

    private static readonly DialogOptions Options = new() { MaxWidth = MaxWidth.Small, FullWidth = true };

    public static Task<bool> AcknowledgeAsync(IDialogService dialogs, AlertService alerts, AlertView alert, Runner run) =>
        ShowAsync(dialogs, alert, "Acknowledge alert", "Acknowledge",
            "The alert stays until its cause recovers or it is resolved. Say what you are doing about it.", required: true,
            comment => run(caller => alerts.AcknowledgeAsync(caller, alert.Id, comment), "The alert was acknowledged."));

    public static Task<bool> ResolveAsync(IDialogService dialogs, AlertService alerts, AlertView alert, Runner run) =>
        ShowAsync(dialogs, alert, "Resolve alert", "Resolve",
            "If the cause is still there, a new alert opens at the next failing result. Say what was done.", required: true,
            comment => run(caller => alerts.ResolveAsync(caller, alert.Id, comment),
                "The alert was resolved. If the cause is still there, a new alert opens."));

    public static Task<bool> EndHoldAsync(IDialogService dialogs, AlertService alerts, AlertView alert, Runner run) =>
        alert.State == AlertState.Resolved
            ? ShowAsync(dialogs, alert, "End hold", "End hold", "The alert is resolved. A new alert opens when the problem returns.",
                required: false, comment => run(caller => alerts.EndHoldAsync(caller, alert.Id, comment), "The hold was ended."))
            : ShowAsync(dialogs, alert, "End hold", "End hold", "The alert is counted and listed as open again.", required: false,
                comment => run(caller => alerts.EndHoldAsync(caller, alert.Id, comment), "The hold was ended. The alert is open again."));

    public static async Task<bool> HoldAsync(IDialogService dialogs, AlertService alerts, TimeDisplay time, AlertView alert, Runner run)
    {
        var parameters = new DialogParameters<HoldAlertDialog>
        {
            { d => d.Alert, alert },
            {
                d => d.OnSubmit, (until, comment) => run(caller => alerts.HoldAsync(caller, alert.Id, until, comment),
                    $"The alert is on hold until {time.Absolute(until)}.")
            }
        };
        var dialog = await dialogs.ShowAsync<HoldAlertDialog>(null, parameters, Options);
        return !(await dialog.Result)!.Canceled;
    }

    private static async Task<bool> ShowAsync(IDialogService dialogs, AlertView alert, string title, string actionLabel, string explanation,
        bool required, Func<string?, Task<bool>> submit)
    {
        var parameters = new DialogParameters<AlertCommentDialog>
        {
            { d => d.Alert, alert },
            { d => d.Title, title },
            { d => d.ActionLabel, actionLabel },
            { d => d.Explanation, explanation },
            { d => d.Required, required },
            { d => d.OnSubmit, submit }
        };
        var dialog = await dialogs.ShowAsync<AlertCommentDialog>(null, parameters, Options);
        return !(await dialog.Result)!.Canceled;
    }
}
