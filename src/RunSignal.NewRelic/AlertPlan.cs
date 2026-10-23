namespace RunSignal.NewRelic;

/// <summary>The kind of problem an alert condition detects.</summary>
public enum AlertKind
{
    /// <summary>A scheduled workload has not succeeded within its schedule plus a grace period, or stopped reporting.</summary>
    Missed,

    /// <summary>A workload that sends heartbeats has not sent one in time, or stopped reporting.</summary>
    Stalled,

    /// <summary>A run took longer than the workload's maximum duration.</summary>
    Overrun,

    /// <summary>Successful runs processed fewer items, on average, than expected.</summary>
    LowOutput,

    /// <summary>A scheduled workload without a failure-rate threshold had a failed run.</summary>
    Failed,

    /// <summary>The share of failed runs exceeded the threshold.</summary>
    FailureRate,

    /// <summary>The share of failed items exceeded the threshold.</summary>
    ItemFailureRate,
}

/// <summary>How a condition aggregates data before evaluating it, matching New Relic's <c>aggregationMethod</c>.</summary>
public enum AggregationMethod
{
    /// <summary>Windows close as data arrives; suits data reported continuously, such as gauges.</summary>
    EventFlow,

    /// <summary>A window closes once no data has arrived for the aggregation timer; suits sparse data, such as runs.</summary>
    EventTimer,
}

/// <summary>One NRQL alert condition RunSignal manages.</summary>
public sealed record AlertCondition
{
    /// <summary>Unique within its policy, such as <c>nightly-invoice: missed</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The workload the condition watches.</summary>
    public required string Workload { get; init; }

    /// <summary>What the condition detects.</summary>
    public required AlertKind Kind { get; init; }

    /// <summary>Shown in the incident; starts with <see cref="AlertPlanner.ManagedMarker"/>.</summary>
    public required string Description { get; init; }

    /// <summary>The NRQL query that produces the evaluated value.</summary>
    public required string Nrql { get; init; }

    /// <summary><c>true</c> to open an incident when the value is above <see cref="Threshold"/>, <c>false</c> when below.</summary>
    public required bool Above { get; init; }

    /// <summary>The threshold the value is compared with.</summary>
    public required double Threshold { get; init; }

    /// <summary>Length of each aggregation window, in seconds.</summary>
    public required int AggregationWindowSeconds { get; init; }

    /// <summary>How data is aggregated into windows.</summary>
    public required AggregationMethod AggregationMethod { get; init; }

    /// <summary>For <see cref="AggregationMethod.EventTimer"/>, seconds without data before a window closes.</summary>
    public int? AggregationTimerSeconds { get; init; }

    /// <summary>
    /// Seconds without any data after which an incident opens (New Relic's loss of signal), or <see langword="null"/>
    /// for none. Used to notice a process that stopped reporting.
    /// </summary>
    public int? LossOfSignalSeconds { get; init; }
}

/// <summary>An alert policy RunSignal manages: one per service and severity, so severities can be routed separately.</summary>
public sealed record AlertPolicyPlan(string Name, Severity Severity, IReadOnlyList<AlertCondition> Conditions);

/// <summary>Every alert policy and condition for one service, produced by <see cref="AlertPlanner.Plan"/>.</summary>
/// <param name="Service">The service name from the manifest.</param>
/// <param name="Policies">One policy per severity, including empty ones so that removed conditions can be deleted.</param>
/// <param name="Warnings">Workloads or expectations that produced no condition, and why.</param>
public sealed record AlertPlan(string Service, IReadOnlyList<AlertPolicyPlan> Policies, IReadOnlyList<string> Warnings);

/// <summary>Tuning for <see cref="AlertPlanner.Plan"/>; shared with the other alert backends.</summary>
public sealed class AlertPlanOptions
{
    /// <inheritdoc cref="RunSignal.Alerting.AlertOptions.MissedGrace"/>
    public TimeSpan MissedGrace { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a workload may report nothing at all before an incident opens. Defaults to 15 minutes.</summary>
    public TimeSpan NotReportingAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <inheritdoc cref="RunSignal.Alerting.AlertOptions.ScheduleTimeZone"/>
    public TimeZoneInfo ScheduleTimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <inheritdoc cref="RunSignal.Alerting.AlertOptions.Now"/>
    public DateTimeOffset? Now { get; set; }

    internal RunSignal.Alerting.AlertOptions ToAlertOptions() => new()
    {
        MissedGrace = MissedGrace,
        NotReportingAfter = NotReportingAfter,
        ScheduleTimeZone = ScheduleTimeZone,
        Now = Now,
    };
}
