using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Fleeto.Web;

/// <summary>Service registrations of fleeto-web, shared by Program and the tests.</summary>
public static class WebServiceRegistration
{
    /// <summary>
    /// Identity options: Argon2id hashing, 12-character minimum without composition rules, lockout after 5 failed attempts for
    /// 15 minutes, unique email. Two-factor authentication is enforced separately for every user.
    /// </summary>
    public static void ConfigureIdentity(IdentityOptions options)
    {
        options.Password.RequiredLength = AccountService.MinimumPasswordLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false;
        options.SignIn.RequireConfirmedAccount = false;
    }

    /// <summary>
    /// The Identity stores use a context created with the system scope, registered as scoped for Identity only. Everything else
    /// creates a context per operation through <see cref="IFleetoDbContextFactory"/>.
    /// </summary>
    public static IdentityBuilder AddFleetoIdentityStores(this IdentityBuilder builder)
    {
        builder.Services.AddScoped(sp => sp.GetRequiredService<IFleetoDbContextFactory>().CreateSystem());
        builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>, Argon2idPasswordHasher>();
        return builder
            .AddUserStore<FleetoUserStore>()
            .AddRoleStore<RoleStore<ApplicationRole, FleetoDbContext, Guid>>()
            .AddDefaultTokenProviders();
    }

    /// <summary>Application services of the UI. Domain services are stateless singletons that create a context per call.</summary>
    public static IServiceCollection AddFleetoWebServices(this IServiceCollection services)
    {
        services.AddSingleton<TwoFactorGate>();
        services.AddSingleton<LiveUpdates>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<TimeDisplay>();
        services.AddScoped<SetupService>();

        services.AddSingleton<DashboardService>();
        services.AddSingleton<ClientService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<SiteService>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<EndpointService>();
        services.AddSingleton<EndpointCheckService>();
        services.AddSingleton<CheckHistoryService>();
        services.AddSingleton<NoteService>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<MaintenanceService>();
        services.AddSingleton<PolicyService>();
        services.AddSingleton<MonitoringTemplateService>();
        services.AddSingleton<ClientTemplateService>();
        services.AddSingleton<UserAdminService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SignInSettingsService>();
        services.AddSingleton<MicrosoftDirectoryService>();
        services.AddSingleton<NotificationChannelService>();
        services.AddSingleton<ScriptService>();
        services.AddSingleton<JobService>();
        services.AddSingleton<AuditQueryService>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<AgentUpdateService>();
        services.AddSingleton<RemoteSessionService>();
        // Integrations (0.4.0): web stores the credentials and asks the workers to use them. It never calls the product
        // itself, because it is on a network without outbound access.
        services.AddSingleton<IntegrationService>();
        services.AddSingleton<PatchService>();
        services.AddScoped<RemoteWindow>();
        return services;
    }
}
