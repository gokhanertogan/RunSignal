using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;

namespace RunSignal.Hangfire;

/// <summary>Connects Hangfire to RunSignal.</summary>
public static class RunSignalHangfireExtensions
{
    /// <summary>Records every Hangfire job run on <paramref name="registry"/>.</summary>
    public static IGlobalConfiguration UseRunSignal(this IGlobalConfiguration configuration, WorkloadRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.UseFilter(new RunSignalHangfireFilter(registry));
    }

    /// <summary>
    /// Records every Hangfire job run on the <see cref="WorkloadRegistry"/> registered by <c>AddRunSignal()</c>,
    /// or on <see cref="WorkloadRegistry.Shared"/> when there is none.
    /// Call it inside <c>services.AddHangfire((sp, config) => config.UseRunSignal(sp))</c>.
    /// </summary>
    public static IGlobalConfiguration UseRunSignal(this IGlobalConfiguration configuration, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return configuration.UseRunSignal(services.GetService<WorkloadRegistry>() ?? WorkloadRegistry.Shared);
    }

    /// <summary>
    /// The workload the current job runs as, for recording items:
    /// add a <c>PerformContext? context = null</c> parameter to the job method and call <c>context.GetWorkload()?.Processed()</c>.
    /// </summary>
    public static Workload? GetWorkload(this PerformContext? context) =>
        context?.Items.TryGetValue(RunSignalHangfireFilter.WorkloadKey, out var workload) == true ? workload as Workload : null;
}
