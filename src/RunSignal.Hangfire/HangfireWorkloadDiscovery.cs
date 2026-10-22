using Hangfire;
using Hangfire.Storage;

namespace RunSignal.Hangfire;

/// <summary>
/// Registers a workload for every Hangfire recurring job, with its cron expression as the schedule, so a recurring
/// job that never starts is still alerted on.
/// </summary>
public sealed class HangfireWorkloadDiscovery : IWorkloadDiscovery
{
    private readonly Func<JobStorage> _storage;

    /// <summary>Discovers recurring jobs in <paramref name="storage"/>, or in <see cref="JobStorage.Current"/> when it is <see langword="null"/>.</summary>
    public HangfireWorkloadDiscovery(JobStorage? storage = null) =>
        _storage = storage is null ? static () => JobStorage.Current : () => storage;

    /// <inheritdoc />
    public Task DiscoverAsync(WorkloadRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        using var connection = _storage().GetConnection();
        foreach (var recurringJob in connection.GetRecurringJobs())
        {
            // Job is null when Hangfire cannot load the job's type, for example after it was renamed.
            if (recurringJob.Job is { } job)
                RunSignalHangfireFilter.Resolve(registry, job.Method, job.Type, recurringJob.Id, () => recurringJob.Cron);
        }

        return Task.CompletedTask;
    }
}
