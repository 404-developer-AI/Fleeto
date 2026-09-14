using Fleetify.Web.Security;
using Fleetify.Web.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Fleetify.Web.Components.Shared;

/// <summary>
/// Base for interactive pages: resolves the <see cref="Security.Caller"/>, runs service calls with one consistent snackbar
/// and error handling (cause and next step; exceptions are logged, never shown), and re-renders when the browser time
/// zone becomes known.
/// </summary>
public abstract class FleetoPageBase : ComponentBase, IDisposable
{
    [Inject] protected CurrentUser CurrentUser { get; set; } = default!;
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] protected TimeDisplay Time { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;

    protected Caller? Caller { get; private set; }

    /// <summary>True when loading the page data failed; the page shows a calm error with the next step.</summary>
    protected bool LoadFailed { get; private set; }

    protected bool Loading { get; set; } = true;

    protected bool CanManage => Caller?.CanManage == true;

    protected bool IsAdmin => Caller?.IsAdmin == true;

    private ILogger Logger => LoggerFactory.CreateLogger(GetType());

    protected override void OnInitialized()
    {
        Time.ZoneChanged += OnZoneChanged;
    }

    /// <summary>Resolves the caller and runs the page's load. Errors are logged and shown as a load failure.</summary>
    protected async Task LoadAsync(Func<Caller, Task> load)
    {
        try
        {
            Caller ??= await CurrentUser.GetAsync();
            await load(Caller);
            LoadFailed = false;
        }
        catch (AccessDeniedException)
        {
            Navigation.NavigateTo("/access-denied");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Loading {Page} failed", GetType().Name);
            LoadFailed = true;
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>
    /// Runs a mutating service call. Shows <paramref name="successMessage"/> on success, the service's problem text on refusal,
    /// and the generic message when an exception occurs. Returns true on success.
    /// </summary>
    protected async Task<bool> RunAsync(Func<Caller, Task<ServiceResult>> action, string? successMessage)
    {
        try
        {
            Caller = await CurrentUser.GetAsync();
            var result = await action(Caller);
            if (result.Success)
            {
                if (successMessage is not null)
                {
                    Snackbar.Add(successMessage, Severity.Success);
                }

                return true;
            }

            Snackbar.Add(result.Problem ?? ServiceSupport.GenericProblem, Severity.Warning);
            return false;
        }
        catch (AccessDeniedException)
        {
            Snackbar.Add(ServiceResult.ForbiddenProblem, Severity.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "An action on {Page} failed", GetType().Name);
            Snackbar.Add(ServiceSupport.GenericProblem, Severity.Error);
            return false;
        }
    }

    /// <summary>Runs a service call that returns a value, when only success matters.</summary>
    protected Task<bool> RunAsync<T>(Func<Caller, Task<ServiceResult<T>>> action, string? successMessage) =>
        RunAsync(async caller => (ServiceResult)await action(caller), successMessage);

    /// <summary>Like <see cref="RunAsync(Func{Caller, Task{ServiceResult}}, string?)"/> for calls that return a value.</summary>
    protected async Task<ServiceResult<T>?> RunForValueAsync<T>(Func<Caller, Task<ServiceResult<T>>> action, string? successMessage)
    {
        ServiceResult<T>? captured = null;
        var success = await RunAsync(async caller =>
        {
            captured = await action(caller);
            return (ServiceResult)captured;
        }, successMessage);
        return success ? captured : null;
    }

    private Func<Task>? _pendingCreate;

    /// <summary>
    /// Remembers that the page was opened with <c>?new=true</c> (the Create menu in the navigation), so its create dialog opens
    /// once the page has loaded. Call from <c>OnParametersSet</c>.
    /// </summary>
    protected void OpenCreateFromQuery(bool requested, Func<Task> create)
    {
        if (requested)
        {
            _pendingCreate = create;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender) => await RunPendingCreateAsync();

    /// <summary>Opens a remembered create dialog after loading and removes <c>new</c> from the address.</summary>
    protected async Task RunPendingCreateAsync()
    {
        if (_pendingCreate is null || Loading || Caller is null)
        {
            return;
        }

        var create = _pendingCreate;
        _pendingCreate = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("new", (string?)null), new NavigationOptions { ReplaceHistoryEntry = true });
        if (CanManage)
        {
            await create();
        }
    }

    private void OnZoneChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Time.ZoneChanged -= OnZoneChanged;
        GC.SuppressFinalize(this);
    }
}
