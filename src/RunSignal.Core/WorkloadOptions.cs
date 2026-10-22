namespace RunSignal;

/// <summary>How urgent alerts for a workload are. Used to route notifications.</summary>
public enum Severity
{
    /// <summary>Worth knowing about; no action needed now.</summary>
    Info,

    /// <summary>Needs attention during working hours.</summary>
    Warning,

    /// <summary>Needs attention now, for example by paging on-call.</summary>
    Critical,
}

/// <summary>
/// What a workload is expected to do. These values describe the alerts RunSignal provisions;
/// they do not change how signals are recorded.
/// </summary>
public sealed class WorkloadOptions
{
    private double? _failureRateThreshold;

    /// <summary>Cron expression for when the workload should run. Produces the <c>missed</c> alert.</summary>
    public string? Schedule { get; set; }

    /// <summary>Longest acceptable run. Produces the <c>overrun</c> alert.</summary>
    public TimeSpan? MaxDuration { get; set; }

    /// <summary>Longest acceptable gap between heartbeats. Produces the <c>stalled</c> alert.</summary>
    public TimeSpan? ExpectEvery { get; set; }

    /// <summary>Fewest items a run should process. Produces the <c>low-output</c> alert.</summary>
    public long? MinProcessed { get; set; }

    /// <summary>Highest acceptable share (0 to 1) of failed runs or items. Produces the <c>failure-rate</c> alert.</summary>
    public double? FailureRateThreshold
    {
        get => _failureRateThreshold;
        set
        {
            if (value is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Must be between 0 and 1.");
            _failureRateThreshold = value;
        }
    }

    /// <summary>How urgent this workload's alerts are. Defaults to <see cref="Severity.Warning"/>.</summary>
    public Severity Severity { get; set; } = Severity.Warning;
}
