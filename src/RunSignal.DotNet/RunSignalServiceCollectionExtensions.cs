using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RunSignal;

/// <summary>Registers RunSignal with dependency injection.</summary>
public static class RunSignalServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="WorkloadRegistry"/>. Add workloads with <see cref="RunSignalBuilder.AddWorkload"/>.
    /// </summary>
    public static RunSignalBuilder AddRunSignal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var registry = new WorkloadRegistry(sp.GetService<IMeterFactory>());

            // Create every declared workload up front so they are all known (and alertable) before their first run.
            foreach (var registration in sp.GetServices<WorkloadRegistration>())
                registry.Get(registration.Name, registration.Configure);
            return registry;
        });

        return new RunSignalBuilder(services);
    }
}

/// <summary>Adds workloads to RunSignal. Returned by <see cref="RunSignalServiceCollectionExtensions.AddRunSignal"/>.</summary>
public sealed class RunSignalBuilder
{
    internal RunSignalBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection RunSignal is registered in.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Declares a workload and registers it as a keyed singleton, so it can be injected with
    /// <c>[FromKeyedServices("name")] Workload workload</c>.
    /// </summary>
    public RunSignalBuilder AddWorkload(string name, Action<WorkloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Services.AddSingleton(new WorkloadRegistration(name, configure));
        Services.AddKeyedSingleton(name, static (sp, key) => sp.GetRequiredService<WorkloadRegistry>().Get((string)key!));
        return this;
    }
}

internal sealed record WorkloadRegistration(string Name, Action<WorkloadOptions>? Configure);
