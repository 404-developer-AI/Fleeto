using Fleeto.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleeto.Infrastructure.Performance;

public static class PerfRegistration
{
    /// <summary>
    /// The performance monitor of one component (0.7.0): the recorder, its settings, the meter listener, the runtime sampler and
    /// the flusher. Every component adds its own collectors on top. The tool measures nothing: it gets the disabled recorder.
    /// </summary>
    public static IServiceCollection AddFleetoPerformance(this IServiceCollection services, FleetoComponent component)
    {
        if (component == FleetoComponent.Tool)
        {
            services.AddSingleton(PerfRecorder.Disabled);
            return services;
        }

        services.AddSingleton<PerfSettingsCache>();
        services.AddSingleton(sp =>
        {
            var recorder = new PerfRecorder(component.PerfName(), sp.GetRequiredService<PerfSettingsCache>(), sp.GetRequiredService<TimeProvider>());
            Perf.Use(recorder);
            PerfStatementTracer.Listen();
            return recorder;
        });
        services.AddSingleton<PerfMeterListener>();
        services.AddSingleton<PerfSettingsLoader>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PerfSettingsLoader>());
        services.AddSingleton<PerfRuntimeSampler>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PerfRuntimeSampler>());
        services.AddSingleton<PerfFlusher>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PerfFlusher>());
        return services;
    }

    /// <summary>The name a component's rows carry: web, gateway, signer or workers. The database trigger matches it to the role.</summary>
    public static string PerfName(this FleetoComponent component) => component.ToString().ToLowerInvariant();
}
