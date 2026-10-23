using System.Collections.Concurrent;
using System.Reflection;
using Quartz;
#if QUARTZ4
using ListenerTask = System.Threading.Tasks.ValueTask;
#else
using ListenerTask = System.Threading.Tasks.Task;
#endif

namespace RunSignal.Quartz;

/// <summary>
/// A Quartz.NET job listener that records every job execution as a run of a RunSignal workload.
/// Register it with <c>q.AddJobListener&lt;RunSignalJobListener&gt;()</c> or
/// <see cref="RunSignalQuartzExtensions.UseRunSignal(IScheduler, WorkloadRegistry)"/>.
/// </summary>
/// <remarks>
/// The workload is named after a <see cref="WorkloadAttribute"/> name on the job class, or else the job key
/// (<c>reports.DailySales</c> becomes <c>reports.daily-sales</c>; the <c>DEFAULT</c> group is left out).
/// A job without an identity, whose key is a random GUID, is named after its class.
/// A cron trigger's expression becomes the workload's schedule unless the attribute sets one, converted from
/// Quartz format to the standard one manifests use (see <see cref="QuartzCron.ToStandard"/>).
/// </remarks>
public sealed class RunSignalJobListener : IJobListener
{
    // Runs in progress, per scheduler and fire instance. Quartz 4 has no per-execution context storage, and every
    // scheduler uses the instance ID "NON_CLUSTERED" by default, so the scheduler name keeps fire IDs apart.
    private static readonly ConcurrentDictionary<string, (Workload Workload, WorkloadRun Run)> s_running = new();

    private readonly WorkloadRegistry _registry;

    // Resolved workloads per job key, so a run costs no reflection.
    private readonly ConcurrentDictionary<JobKey, Workload> _workloads = new();

    /// <summary>Creates a listener that records on <paramref name="registry"/>.</summary>
    public RunSignalJobListener(WorkloadRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public string Name => "RunSignal";

    /// <inheritdoc />
    public ListenerTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var workload = _workloads.GetOrAdd(context.JobDetail.Key, static (_, state) => Resolve(state.Registry, state.Context.JobDetail, state.Context.Trigger as ICronTrigger), (Registry: _registry, Context: context));
        s_running[RunId(context)] = (workload, workload.Start());
        return ListenerTask.CompletedTask;
    }

    /// <inheritdoc />
    public ListenerTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (!s_running.TryRemove(RunId(context), out var running))
            return ListenerTask.CompletedTask;
        var run = running.Run;

        if (jobException is null)
        {
            run.Succeed();
            return ListenerTask.CompletedTask;
        }

        // Quartz wraps an exception thrown by the job in a SchedulerException inside a JobExecutionException
        // (JobExecutionException derives from SchedulerException); report the job's own exception.
        Exception exception = jobException;
        while (exception is SchedulerException { InnerException: { } inner })
            exception = inner;
        if (exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            run.Cancel();
        else
            run.Fail(exception);
        return ListenerTask.CompletedTask;
    }

    /// <inheritdoc />
    public ListenerTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        s_running.TryRemove(RunId(context), out _);
        return ListenerTask.CompletedTask;
    }

    internal static Workload? CurrentWorkload(IJobExecutionContext context) =>
        s_running.TryGetValue(RunId(context), out var running) ? running.Workload : null;

    private static string RunId(IJobExecutionContext context) => context.Scheduler.SchedulerName + "/" + context.FireInstanceId;

    // Shared with QuartzWorkloadDiscovery so a job gets the same workload whether it was discovered or ran first.
    internal static Workload Resolve(WorkloadRegistry registry, IJobDetail job, ICronTrigger? cronTrigger)
    {
        var key = job.Key;
        var (jobClass, className) = JobClass(job);
        var attribute = jobClass?.GetCustomAttribute<WorkloadAttribute>();

        // Jobs scheduled without an identity get a random GUID key, which would create a new workload on every
        // start; name those after the job class instead.
        var keyName = Guid.TryParse(key.Name, out _) ? className : key.Name;
        var name = attribute?.Name
            ?? WorkloadRegistry.ToWorkloadName(key.Group == JobKey.DefaultGroup ? keyName : $"{key.Group}.{keyName}");

        return registry.GetOrAdd(name, options =>
        {
            attribute?.ApplyTo(options);
            if (options.Schedule is null && cronTrigger?.CronExpressionString is { } cron)
                options.Schedule = QuartzCron.ToStandard(cron);
        });
    }

    // Quartz 4 refers to job classes by name and resolves them on demand; Quartz 3 holds the Type.
    private static (Type? Type, string Name) JobClass(IJobDetail job)
    {
#if QUARTZ4
        // FullName may be assembly-qualified ("Shop.Jobs.SyncJob, Shop.Worker"): drop the assembly first.
        var typeName = job.JobType.FullName.Split(',')[0].Trim();
        return (job.JobType.TryResolve(out var type) ? type : null, typeName[(typeName.LastIndexOfAny(['.', '+']) + 1)..]);
#else
        return (job.JobType, job.JobType.Name);
#endif
    }
}
