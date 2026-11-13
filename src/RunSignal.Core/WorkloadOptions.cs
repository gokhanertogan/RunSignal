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
    private string? _owner;
    private string? _runbookUrl;

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

    /// <summary>
    /// Who answers this workload's alerts, a team or person such as <c>billing-team</c> (at most 128 characters).
    /// Added to every alert's description, and as the <c>owner</c> label on Prometheus rules for routing.
    /// </summary>
    public string? Owner
    {
        get => _owner;
        set
        {
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128))
                throw new ArgumentException("An owner must be 1 to 128 characters and not blank.", nameof(value));
            _owner = value;
        }
    }

    /// <summary>
    /// An absolute http or https URL of what to do when one of this workload's alerts fires. Added to every alert's
    /// description, and as the <c>runbook_url</c> annotation on Prometheus rules.
    /// </summary>
    public string? RunbookUrl
    {
        get => _runbookUrl;
        set
        {
            if (value is not null && !IsWebUrl(value))
                throw new ArgumentException($"Runbook URL '{value}' must be an absolute http or https URL.", nameof(value));
            _runbookUrl = value;
        }
    }

    internal static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
