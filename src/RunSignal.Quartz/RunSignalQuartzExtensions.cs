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

    /// <summary>The workload the current job runs as, for recording items: <c>context.GetWorkload()?.Processed()</c>.</summary>
    public static Workload? GetWorkload(this IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Get(RunSignalJobListener.WorkloadKey) as Workload;
    }
}
