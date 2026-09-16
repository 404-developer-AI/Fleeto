using Fleeto.Signer.Handlers;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Signer;

public static class SignerServiceCollectionExtensions
{
    /// <summary>Registers the key ring, the bootstrapper, the request handlers and the signing loop.</summary>
    public static IServiceCollection AddFleetoSigner(this IServiceCollection services)
    {
        services.AddSingleton<SignerKeyRing>();
        services.AddSingleton<SigningKeyBootstrapper>();
        services.AddSingleton<SigningRateLimiter>();
        services.AddSingleton<AgentConfigSigner>();

        services.AddSingleton<ISigningRequestHandler, AgentEnrollmentHandler>();
        services.AddSingleton<ISigningRequestHandler, AgentRenewalHandler>();
        services.AddSingleton<ISigningRequestHandler, AgentRecoveryHandler>();
        services.AddSingleton<ISigningRequestHandler, GatewayCertificateHandler>();
        services.AddSingleton<ISigningRequestHandler, AgentConfigHandler>();
        services.AddSingleton<ISigningRequestHandler, JobHandler>();
        services.AddSingleton<ISigningRequestHandler, WatchdogCertificateHandler>();
        services.AddSingleton<ISigningRequestHandler, RemoteSessionTokenHandler>();

        services.AddSingleton<SigningRequestProcessor>();
        services.AddHostedService<SigningService>();
        return services;
    }
}
