using Fleetify.Infrastructure;
using Fleetify.Infrastructure.Hosting;
using Fleetify.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Container health check: no port to probe, so the loops keep a heartbeat file fresh while they are healthy.
if (args is ["healthcheck"])
{
    return HeartbeatFile.Check(FleetifyComponent.Workers);
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

// Workers component: the root key (settings, SMTP and backup credentials, the license document) and the workers
// database role. No signer key.
builder.Services.AddFleetifyInfrastructure(builder.Configuration, FleetifyComponent.Workers);
builder.Services.AddFleetifyWorkers(builder.Configuration);

using var host = builder.Build();
await host.RunAsync();
return Environment.ExitCode;
