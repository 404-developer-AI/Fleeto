using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Backups;
using Fleeto.Workers.Checks;
using Fleeto.Workers.Configuration;
using Fleeto.Infrastructure.Email;
using Fleeto.Workers.Email;
using Fleeto.Workers.Endpoints;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Integrations;
using Fleeto.Workers.Jobs;
using Fleeto.Workers.Licensing;
using Fleeto.Workers.Options;
using Fleeto.Workers.Remote;
using Fleeto.Workers.Retention;
using Fleeto.Workers.SignIn;
using Fleeto.Workers.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers;

public static class WorkersServiceCollectionExtensions
{
    /// <summary>Registers the workers loops and their helpers. Call after <c>AddFleetoInfrastructure(..., Workers)</c>.</summary>
    public static IServiceCollection AddFleetoWorkers(this IServiceCollection services, IConfiguration configuration)
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
        services.AddSingleton<NotificationBundler>();
        services.AddSingleton<IEmailTransportFactory, EmailTransportFactory>();
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();
        services.AddSingleton<IPgDumpTargetProvider, PgDumpTargetProvider>();
        services.AddSingleton<PgDumpRunner>();
        services.AddSingleton<BackupDestinations>();
        // Integrations (0.4.0): the workers are the only containers with outbound access, so every call to an external
        // product is made here, with the whole request budget of the instance.
        services.AddSingleton(sp => new Action1ClientFactory(sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(), IntegrationBudgets.WorkerRequestsPerMinute));

        // Sign-in with Entra ID (0.5.0): the metadata and signing keys of the tenant are cached here, so they are read
        // once rather than per sign-in.
        services.AddSingleton(sp => new EntraSignInClientFactory(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp => new MicrosoftGraphClient(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<MicrosoftGraphClient>>()));

        services.AddHostedService<HeartbeatFileService>();
        services.AddHostedService<ConfigChangeFanoutService>();
        services.AddHostedService<CheckEvaluationService>();
        services.AddHostedService<CheckRunRequestService>();
        services.AddHostedService<JobMaintenanceService>();
        services.AddHostedService<RemoteSessionMaintenanceService>();
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
        services.AddHostedService<RetentionService>();
        services.AddHostedService<IntegrationSyncService>();
        services.AddHostedService<IntegrationFollowService>();
        services.AddHostedService<PatchAutomationService>();
        services.AddHostedService<PatchSyncService>();
        services.AddHostedService<PatchDeploymentService>();
        services.AddHostedService<SignInExchangeService>();
        services.AddHostedService<MicrosoftRequestService>();

        return services;
    }
}
