namespace RunSignal;

/// <summary>
/// Finds workloads before they first run, such as Hangfire recurring jobs or Quartz.NET triggers, and
/// registers them so their schedules can be alerted on even if they never start.
/// </summary>
public interface IWorkloadDiscovery
{
    /// <summary>Registers the workloads this discovery knows about in <paramref name="registry"/>.</summary>
    Task DiscoverAsync(WorkloadRegistry registry, CancellationToken cancellationToken);
}
