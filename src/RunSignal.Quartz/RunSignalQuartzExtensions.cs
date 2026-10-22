using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;

namespace RunSignal.Quartz;

/// <summary>Connects Quartz.NET to RunSignal.</summary>
public static class RunSignalQuartzExtensions
{
    /// <summary>Records every job execution of <paramref name="scheduler"/> on <paramref name="registry"/>.</summary>
    public static IScheduler UseRunSignal(this IScheduler scheduler, WorkloadRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        scheduler.ListenerManager.AddJobListener(new RunSignalJobListener(registry), GroupMatcher<JobKey>.AnyGroup());
        return scheduler;
    }

    /// <summary>
    /// Registers every job of <paramref name="scheduler"/> as a workload, so jobs that never fire are alerted on.
    /// Not needed with dependency injection: use <see cref="AddQuartz(RunSignalBuilder)"/>.
    /// </summary>
    public static Task DiscoverRunSignalWorkloadsAsync(this IScheduler scheduler, WorkloadRegistry registry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        return QuartzWorkloadDiscovery.DiscoverAsync(scheduler, registry, cancellationToken);
    }

    /// <summary>
    /// Registers every job of the application's Quartz.NET schedulers as a workload when the application starts,
    /// so jobs that never fire are alerted on and appear in the manifest. Uses the <see cref="ISchedulerFactory"/>
    /// from dependency injection. Jobs are still recorded only after <c>q.AddJobListener&lt;RunSignalJobListener&gt;()</c>.
    /// </summary>
    public static RunSignalBuilder AddQuartz(this RunSignalBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IWorkloadDiscovery>(sp => new QuartzWorkloadDiscovery(sp.GetRequiredService<ISchedulerFactory>()));
        return builder;
    }

    /// <summary>The workload the current job runs as, for recording items: <c>context.GetWorkload()?.Processed()</c>.</summary>
    public static Workload? GetWorkload(this IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Get(RunSignalJobListener.WorkloadKey) as Workload;
    }
}
