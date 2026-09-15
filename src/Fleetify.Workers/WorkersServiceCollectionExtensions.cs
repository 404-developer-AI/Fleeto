using Fleetify.Workers.Alerts;
using Fleetify.Workers.Backups;
using Fleetify.Workers.Checks;
using Fleetify.Workers.Configuration;
using Fleetify.Workers.Email;
using Fleetify.Workers.Endpoints;
using Fleetify.Workers.Hosting;
using Fleetify.Workers.Jobs;
using Fleetify.Workers.Licensing;
using Fleetify.Workers.Options;
using Fleetify.Workers.Retention;
using Fleetify.Workers.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleetify.Workers;

public static class WorkersServiceCollectionExtensions
{
    /// <summary>Registers the workers loops and their helpers. Call after <c>AddFleetifyInfrastructure(..., Workers)</c>.</summary>
    public static IServiceCollection AddFleetifyWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.SectionName));
        services.Configure<CheckEvaluationOptions>(configuration.GetSection(CheckEvaluationOptions.SectionName));
        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.SectionName));

        // Why: a failing loop must never stop the host; WorkerLoop catches everything, this is the second line.
        services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<WorkerHeartbeat>();
        services.AddSingleton<AlertNotificationService>();
        services.AddSingleton<IEmailTransportFactory, EmailTransportFactory>();
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();
        services.AddSingleton<IPgDumpTargetProvider, PgDumpTargetProvider>();
        services.AddSingleton<PgDumpRunner>();
        services.AddSingleton<BackupDestinations>();

        services.AddHostedService<HeartbeatFileService>();
        services.AddHostedService<ConfigChangeFanoutService>();
        services.AddHostedService<CheckEvaluationService>();
        services.AddHostedService<CheckRunRequestService>();
        services.AddHostedService<JobMaintenanceService>();
        services.AddHostedService<AlertHoldService>();
        services.AddHostedService<EndpointHealthService>();
        services.AddHostedService<EndpointEventService>();
        services.AddHostedService<MaintenanceExpiryService>();
        services.AddHostedService<MaintenanceWindowService>();
        services.AddHostedService<OutboxEmailService>();
        services.AddHostedService<OutboxWebhookService>();
        services.AddHostedService<LicenseMonitorService>();
        services.AddHostedService<CredentialExpiryService>();
        services.AddHostedService<BackupService>();
        services.AddHostedService<WalShipperService>();
        services.AddHostedService<RetentionService>();

        return services;
    }
}
