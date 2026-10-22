using System.Collections.Concurrent;
using System.Reflection;
using Hangfire.Common;
using Hangfire.Server;

namespace RunSignal.Hangfire;

/// <summary>
/// A Hangfire server filter that records every job run as a run of a RunSignal workload.
/// Register it with <see cref="RunSignalHangfireExtensions.UseRunSignal(global::Hangfire.IGlobalConfiguration, WorkloadRegistry)"/>.
/// </summary>
/// <remarks>
/// The workload is named, in order of preference, after a <see cref="WorkloadAttribute"/> name on the method or
/// class, the recurring job ID, or the job's class and method (<c>InvoiceJobs.Generate</c> becomes
/// <c>invoice-jobs.generate</c>). A recurring job's cron expression becomes the workload's schedule unless the
/// attribute sets one.
/// </remarks>
public sealed class RunSignalHangfireFilter : JobFilterAttribute, IServerFilter
{
    internal const string RunKey = "RunSignal.Run";
    internal const string WorkloadKey = "RunSignal.Workload";
    private const string RecurringJobIdParameter = "RecurringJobId";

    private readonly WorkloadRegistry _registry;

    // Resolved workloads per job method and recurring job ID, so a run costs no reflection or storage access.
    private readonly ConcurrentDictionary<(MethodInfo Method, Type Type, string? RecurringJobId), Workload> _workloads = new();

    /// <summary>Creates a filter that records on <paramref name="registry"/>.</summary>
    public RunSignalHangfireFilter(WorkloadRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        var job = context.BackgroundJob.Job;
        if (job is null)
            return;

        var recurringJobId = GetRecurringJobId(context);
        var workload = _workloads.GetOrAdd(
            (job.Method, job.Type, recurringJobId),
            static (key, state) => Resolve(state.Registry, key.Method, key.Type, key.RecurringJobId, () => GetRecurringJobCron(state.Context, key.RecurringJobId!)),
            (Registry: _registry, Context: context));

        context.Items[WorkloadKey] = workload;
        context.Items[RunKey] = workload.Start();
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
        if (!context.Items.TryGetValue(RunKey, out var value) || value is not WorkloadRun run)
            return;

        var exception = context.Exception;
        if (exception is null)
        {
            run.Succeed();
            return;
        }

        // Hangfire wraps exceptions thrown by the job in a JobPerformanceException.
        if (exception is JobPerformanceException { InnerException: { } inner })
            exception = inner;

        if (exception is JobAbortedException ||
            (exception is OperationCanceledException && context.CancellationToken.ShutdownToken.IsCancellationRequested))
            run.Cancel();
        else
            run.Fail(exception);
    }

    // Shared with HangfireWorkloadDiscovery so a job gets the same workload whether it was discovered or ran first.
    internal static Workload Resolve(WorkloadRegistry registry, MethodInfo method, Type type, string? recurringJobId, Func<string?> getCron)
    {
        var attribute = method.GetCustomAttribute<WorkloadAttribute>() ?? type.GetCustomAttribute<WorkloadAttribute>();
        var name = attribute?.Name
            ?? (recurringJobId is not null ? WorkloadRegistry.ToWorkloadName(recurringJobId) : WorkloadRegistry.ToWorkloadName($"{type.Name}.{method.Name}"));

        return registry.GetOrAdd(name, options =>
        {
            attribute?.ApplyTo(options);
            if (options.Schedule is null && recurringJobId is not null)
                options.Schedule = getCron();
        });
    }

    private static string? GetRecurringJobId(PerformContext context)
    {
        // The parameters snapshot avoids a storage round trip; older storages may not provide it.
        if (context.BackgroundJob.ParametersSnapshot is { } snapshot)
            return snapshot.TryGetValue(RecurringJobIdParameter, out var json) && json is not null ? SerializationHelper.Deserialize<string>(json) : null;
        return context.GetJobParameter<string>(RecurringJobIdParameter);
    }

    private static string? GetRecurringJobCron(PerformContext context, string recurringJobId)
    {
        try
        {
            var entries = context.Connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");
            return entries is not null && entries.TryGetValue("Cron", out var cron) ? cron : null;
        }
        catch (Exception)
        {
            // The schedule only improves alerts; never fail a job because it could not be read.
            return null;
        }
    }
}
