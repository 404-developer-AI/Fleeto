using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Hosting;
using Fleetify.Signer.Keys;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleetify.Signer.Processing;

/// <summary>
/// The signer's main loop: bootstraps the keys, then processes signing requests when notified and at least every
/// 15 seconds, so a lost notification only delays work. Touches the heartbeat file on every loop.
/// </summary>
public sealed class SigningService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly SigningKeyBootstrapper _bootstrapper;
    private readonly SigningRequestProcessor _processor;
    private readonly INotificationBus _bus;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SigningService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public SigningService(SigningKeyBootstrapper bootstrapper, SigningRequestProcessor processor, INotificationBus bus,
        IHostApplicationLifetime lifetime, ILogger<SigningService> logger)
    {
        _bootstrapper = bootstrapper;
        _processor = processor;
        _bus = bus;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await BootstrapAsync(stoppingToken))
        {
            return;
        }

        // Subscribe before the first batch, so nothing inserted in between is missed. Resync also just wakes the loop.
        using var subscription = _bus.Subscribe(NotificationChannels.SigningRequests, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

        _logger.LogInformation("Signer is processing signing requests");
        var errorBackoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            HeartbeatFile.Touch(FleetifyComponent.Signer);

            BatchResult result;
            try
            {
                result = await _processor.ProcessBatchAsync(stoppingToken);
                errorBackoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Processing signing requests failed; retrying in {Delay}", errorBackoff);
                await DelayAsync(errorBackoff, stoppingToken);
                errorBackoff = TimeSpan.FromSeconds(Math.Min(MaxErrorBackoff.TotalSeconds, errorBackoff.TotalSeconds * 2));
                continue;
            }

            if (result.RateLimited > 0)
            {
                // Leave room for the sliding window to move instead of spinning on requests that must wait.
                await DelayAsync(RateLimitBackoff, stoppingToken);
                continue;
            }

            if (result.Handled >= SigningRequestProcessor.BatchSize)
            {
                continue;
            }

            try
            {
                await _wake.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    private async Task<bool> BootstrapAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _bootstrapper.RunAsync(stoppingToken);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (SignerKeyMismatchException ex)
            {
                // Never retried and never "fixed" by generating keys: that would break every enrolled agent.
                _logger.LogCritical(ex, "{Message}", ex.Message);
                Environment.ExitCode = 1;
                _lifetime.StopApplication();
                return false;
            }
            catch (Exception ex)
            {
                // Typically the database is not reachable yet during stack start. The heartbeat is not touched, so the
                // container reports unhealthy until the keys are loaded.
                _logger.LogError(ex, "Could not load the signer keys; retrying in {Delay}", delay);
                await DelayAsync(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(MaxErrorBackoff.TotalSeconds, delay.TotalSeconds * 2));
            }
        }

        return false;
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
