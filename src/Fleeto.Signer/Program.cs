using Fleeto.Infrastructure;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Signer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Container health check: no port to probe, so the signing loop touches a heartbeat file instead.
if (args is ["healthcheck"])
{
    return HeartbeatFile.Check(FleetoComponent.Signer);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Why: the container may start the process from another working directory; appsettings live next to the binary.
    ContentRootPath = AppContext.BaseDirectory
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

// Signer component: the signer key and the signer database role. No root key, so this process cannot decrypt
// any other secret of the instance.
builder.Services.AddFleetoInfrastructure(builder.Configuration, FleetoComponent.Signer);
builder.Services.AddFleetoSigner();

using var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;
