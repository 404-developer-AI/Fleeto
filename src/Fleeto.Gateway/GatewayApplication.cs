using System.Net;
using Fleeto.Gateway.Data;
using Fleeto.Gateway.Diagnostics;
using Fleeto.Gateway.Enrollment;
using Fleeto.Gateway.Releases;
using Fleeto.Gateway.Remote;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Signing;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace Fleeto.Gateway;

/// <summary>Builds the gateway host. Separate from Program so tests can start the real host with their own services.</summary>
public static class GatewayApplication
{
    /// <param name="configureServices">Runs after the gateway services are registered; tests replace the data source here.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });

        builder.Services.AddFleetoInfrastructure(builder.Configuration, FleetoComponent.Gateway);
        builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));

        builder.Services.AddSingleton<GatewayMetrics>();
        builder.Services.AddSingleton<GatewayStore>();
        builder.Services.AddSingleton<SigningRequestClient>();
        builder.Services.AddSingleton<EnrollmentTokenValidator>();
        builder.Services.AddSingleton<EnrollmentHandler>();
        builder.Services.AddSingleton<RecoveryHandler>();
        builder.Services.AddSingleton<AgentConnectionHandler>();
        builder.Services.AddSingleton<GatewayHealth>();
        builder.Services.AddSingleton<AgentTlsOptions>();
        builder.Services.AddSingleton<ProxyProtocolMiddleware>();
        builder.Services.AddSingleton<ReleaseDownloadHandler>();

        // Hosted singletons: resolvable by type and started by the host. Order matters for shutdown (reverse order):
        // the session manager stops before the allow list and the certificate provider.
        builder.Services.AddSingleton<CertificateAllowList>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CertificateAllowList>());
        builder.Services.AddSingleton<GatewayCertificateProvider>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayCertificateProvider>());
        builder.Services.AddSingleton<ReleaseCatalog>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReleaseCatalog>());
        builder.Services.AddSingleton<AgentSessionManager>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentSessionManager>());
        builder.Services.AddSingleton<RemoteRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteRelay>());
        builder.Services.AddHostedService<LastSeenFlusher>();
        builder.Services.AddHostedService<GatewaySummaryLogger>();

        var gatewayOptions = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        if (gatewayOptions.ProxyProtocol.Enabled && ProxyProtocolMiddleware.ParseNetworks(gatewayOptions.ProxyProtocol.TrustedNetworks).Count == 0)
        {
            throw new InvalidOperationException(
                "Gateway:ProxyProtocol is enabled without trusted networks. Set Gateway:ProxyProtocol:TrustedNetworks to the network the host proxy connects from.");
        }
        EnrollmentHandler.AddRateLimiting(builder.Services, gatewayOptions.EnrollmentsPerMinutePerAddress);

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));

        configureServices?.Invoke(builder);

        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var options = context.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
            var address = IPAddress.Parse(options.ListenAddress);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = ProtocolLimits.MaxEnrollRequestBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);

            kestrel.Listen(address, options.AgentPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                // Before TLS: the PROXY protocol header precedes the ClientHello.
                listen.Use(next => connection =>
                    kestrel.ApplicationServices.GetRequiredService<ProxyProtocolMiddleware>().OnConnectionAsync(connection, next));
                listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                    OnConnection = callback => kestrel.ApplicationServices.GetRequiredService<AgentTlsOptions>().OnConnectionAsync(callback)
                });
            });

            kestrel.Listen(address, options.HealthPort, listen => listen.Protocols = HttpProtocols.Http1);

            // Browser side of the remote session relay (0.3.0), behind the host proxy.
            kestrel.Listen(address, options.RelayPort, listen => listen.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        var ports = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;

        app.UseWebSockets();
        app.UseRateLimiter();

        // Separate by the local port, not RequireHost: behind the SNI passthrough the agent's Host header carries no port.
        app.MapPost(ProtocolLimits.EnrollPath, (HttpContext context, EnrollmentHandler handler) => handler.HandleAsync(context))
            .AddEndpointFilter(OnlyOnPort(ports.AgentPort))
            .RequireRateLimiting(EnrollmentHandler.RateLimitPolicy);
        app.MapGet(ProtocolLimits.ConnectPath, (HttpContext context, AgentConnectionHandler handler) => handler.HandleAsync(context))
            .AddEndpointFilter(OnlyOnPort(ports.AgentPort));
        app.MapGet(ProtocolLimits.ReleasesPath + "/{version}/{**file}",
                (HttpContext context, string version, string file, ReleaseDownloadHandler handler) => handler.HandleAsync(context, version, file))
            .AddEndpointFilter(OnlyOnPort(ports.AgentPort));
        app.MapPost(ProtocolLimits.RecoverPath, (HttpContext context, RecoveryHandler handler) => handler.HandleAsync(context))
            .AddEndpointFilter(OnlyOnPort(ports.AgentPort))
            .RequireRateLimiting(EnrollmentHandler.RateLimitPolicy);
        // Public: the instance CA certificates (PEM). TLS stacks leave a self-signed root out of the handshake, so before
        // enrollment the agent fetches the CA here, matches it against the install fingerprint and only then connects
        // with normal TLS verification against it. Nothing secret is exchanged before that check.
        app.MapGet(ProtocolLimits.CaPath, (CertificateAllowList allowList) =>
            {
                var pem = string.Concat(allowList.CaCertificates.Select(c => c.ExportCertificatePem() + "\n"));
                return pem.Length == 0
                    ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        detail: "The gateway has not loaded the instance CA yet. Try again in a minute.")
                    : Results.Text(pem, "application/x-pem-file");
            })
            .AddEndpointFilter(OnlyOnPort(ports.AgentPort))
            .RequireRateLimiting(EnrollmentHandler.RateLimitPolicy);
        app.MapGet("/health", (HttpContext context, GatewayHealth health) => health.HandleAsync(context))
            .AddEndpointFilter(OnlyOnPort(ports.HealthPort));
        // Remote sessions (0.3.0): the browser presents its token on the relay port, the endpoint connects with its certificate on the agent port.
        app.MapGet(RemoteRelay.BrowserPathPrefix + "{participantId:guid}", (HttpContext context, Guid participantId, RemoteRelay relay) =>
                relay.HandleBrowserAsync(context, participantId))
            .AddEndpointFilter(OnlyOnPort(ports.RelayPort));
        app.MapGet(RemoteRelay.EndpointPathPrefix + "{participantId:guid}", (HttpContext context, Guid participantId, RemoteRelay relay) =>
                relay.HandleEndpointAsync(context, participantId))
            .AddEndpointFilter(OnlyOnPort(ports.AgentPort));

        return app;
    }

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> OnlyOnPort(int port) =>
        (invocation, next) => invocation.HttpContext.Connection.LocalPort == port
            ? next(invocation)
            : ValueTask.FromResult<object?>(Results.NotFound());
}
