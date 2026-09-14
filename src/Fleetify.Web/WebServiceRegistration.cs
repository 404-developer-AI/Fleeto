using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Identity;
using Fleetify.Web.Security;
using Fleetify.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Fleetify.Web;

/// <summary>Service registrations of fleetify-web, shared by Program and the tests.</summary>
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
    /// creates a context per operation through <see cref="IFleetifyDbContextFactory"/>.
    /// </summary>
    public static IdentityBuilder AddFleetifyIdentityStores(this IdentityBuilder builder)
    {
        builder.Services.AddScoped(sp => sp.GetRequiredService<IFleetifyDbContextFactory>().CreateSystem());
        builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>, Argon2idPasswordHasher>();
        return builder
            .AddUserStore<FleetifyUserStore>()
            .AddRoleStore<RoleStore<ApplicationRole, FleetifyDbContext, Guid>>()
            .AddDefaultTokenProviders();
    }

    /// <summary>Application services of the UI. Domain services are stateless singletons that create a context per call.</summary>
    public static IServiceCollection AddFleetifyWebServices(this IServiceCollection services)
    {
        services.AddSingleton<TwoFactorGate>();
        services.AddSingleton<LiveUpdates>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<TimeDisplay>();
        services.AddScoped<SetupService>();

        services.AddSingleton<DashboardService>();
        services.AddSingleton<ClientService>();
        services.AddSingleton<SiteService>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<EndpointService>();
        services.AddSingleton<EndpointCheckService>();
        services.AddSingleton<NoteService>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<PolicyService>();
        services.AddSingleton<MonitoringTemplateService>();
        services.AddSingleton<ClientTemplateService>();
        services.AddSingleton<UserAdminService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuditQueryService>();
        return services;
    }
}
