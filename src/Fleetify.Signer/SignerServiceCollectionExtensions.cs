using Fleetify.Signer.Handlers;
using Fleetify.Signer.Keys;
using Fleetify.Signer.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Signer;

public static class SignerServiceCollectionExtensions
{
    /// <summary>Registers the key ring, the bootstrapper, the request handlers and the signing loop.</summary>
    public static IServiceCollection AddFleetifySigner(this IServiceCollection services)
    {
        services.AddSingleton<SignerKeyRing>();
        services.AddSingleton<SigningKeyBootstrapper>();
        services.AddSingleton<SigningRateLimiter>();
        services.AddSingleton<AgentConfigSigner>();

        services.AddSingleton<ISigningRequestHandler, AgentEnrollmentHandler>();
        services.AddSingleton<ISigningRequestHandler, AgentRenewalHandler>();
        services.AddSingleton<ISigningRequestHandler, GatewayCertificateHandler>();
        services.AddSingleton<ISigningRequestHandler, AgentConfigHandler>();

        services.AddSingleton<SigningRequestProcessor>();
        services.AddHostedService<SigningService>();
        return services;
    }
}
