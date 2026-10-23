using Quartz;
#if QUARTZ4
using Quartz.Extensibility;
#else
using Quartz.Impl.Matchers;
#endif

namespace RunSignal.Quartz;

/// <summary>
/// Registers a workload for every job of the Quartz.NET schedulers, with the first cron trigger's expression as the
/// schedule, so a job that never fires is still alerted on.
/// </summary>
public sealed class QuartzWorkloadDiscovery : IWorkloadDiscovery
{
#if QUARTZ4
    // Quartz 4 registers a factory per named scheduler; the repository holds every scheduler instance.
    private readonly ISchedulerRepository _schedulers;

    /// <summary>Discovers the jobs of every scheduler in <paramref name="schedulers"/>.</summary>
    public QuartzWorkloadDiscovery(ISchedulerRepository schedulers)
    {
        ArgumentNullException.ThrowIfNull(schedulers);
        _schedulers = schedulers;
    }

    /// <inheritdoc />
    public async Task DiscoverAsync(WorkloadRegistry registry, CancellationToken cancellationToken)
    {
        foreach (var scheduler in _schedulers.LookupAll())
            await DiscoverAsync(scheduler, registry, cancellationToken).ConfigureAwait(false);
    }
#else
    private readonly ISchedulerFactory _schedulerFactory;

    /// <summary>Discovers the jobs of every scheduler <paramref name="schedulerFactory"/> has created.</summary>
    public QuartzWorkloadDiscovery(ISchedulerFactory schedulerFactory)
    {
        ArgumentNullException.ThrowIfNull(schedulerFactory);
        _schedulerFactory = schedulerFactory;
    }

    /// <inheritdoc />
    public async Task DiscoverAsync(WorkloadRegistry registry, CancellationToken cancellationToken)
    {
        foreach (var scheduler in await _schedulerFactory.GetAllSchedulers(cancellationToken).ConfigureAwait(false))
            await DiscoverAsync(scheduler, registry, cancellationToken).ConfigureAwait(false);
    }
#endif

    internal static async Task DiscoverAsync(IScheduler scheduler, WorkloadRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken).ConfigureAwait(false))
        {
            var job = await scheduler.GetJobDetail(key, cancellationToken).ConfigureAwait(false);
            if (job is null)
                continue;

            var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken).ConfigureAwait(false);
            RunSignalJobListener.Resolve(registry, job, triggers.OfType<ICronTrigger>().FirstOrDefault());
        }
    }
}
