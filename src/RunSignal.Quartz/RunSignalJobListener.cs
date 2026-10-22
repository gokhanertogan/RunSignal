using System.Collections.Concurrent;
using System.Reflection;
using Quartz;

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
/// A cron trigger's expression becomes the workload's schedule unless the attribute sets one; it is kept in
/// Quartz format (with seconds).
/// </remarks>
public sealed class RunSignalJobListener : IJobListener
{
    internal const string RunKey = "RunSignal.Run";
    internal const string WorkloadKey = "RunSignal.Workload";

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
    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var workload = _workloads.GetOrAdd(context.JobDetail.Key, static (key, state) => state.Listener.Resolve(key, state.Context), (Listener: this, Context: context));
        context.Put(WorkloadKey, workload);
        context.Put(RunKey, workload.Start());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (context.Get(RunKey) is not WorkloadRun run)
            return Task.CompletedTask;

        if (jobException is null)
        {
            run.Succeed();
            return Task.CompletedTask;
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
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private Workload Resolve(JobKey key, IJobExecutionContext context)
    {
        var attribute = context.JobDetail.JobType.GetCustomAttribute<WorkloadAttribute>();
        // Jobs scheduled without an identity get a random GUID key, which would create a new workload on every
        // start; name those after the job class instead.
        var keyName = Guid.TryParse(key.Name, out _) ? context.JobDetail.JobType.Name : key.Name;
        var name = attribute?.Name
            ?? WorkloadRegistry.ToWorkloadName(key.Group == JobKey.DefaultGroup ? keyName : $"{key.Group}.{keyName}");

        return _registry.GetOrAdd(name, options =>
        {
            attribute?.ApplyTo(options);
            if (options.Schedule is null && context.Trigger is ICronTrigger cron)
                options.Schedule = cron.CronExpressionString;
        });
    }
}
