namespace RunSignal.Alerting;

/// <summary>Tuning shared by every alert backend.</summary>
public sealed class AlertOptions
{
    /// <summary>
    /// Extra time a scheduled workload gets, on top of the longest gap in its schedule, before it counts as missed.
    /// A workload's max duration is used instead when it has one. Defaults to 15 minutes.
    /// </summary>
    public TimeSpan MissedGrace { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a workload may report nothing at all before it counts as not reporting. Defaults to 15 minutes.</summary>
    public TimeSpan NotReportingAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The time zone cron schedules are evaluated in. Defaults to UTC.</summary>
    public TimeZoneInfo ScheduleTimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>When to start reading schedules from. Defaults to now; set it for repeatable output in tests.</summary>
    public DateTimeOffset? Now { get; set; }
}

/// <summary>When a scheduled workload counts as missed: the longest gap in its schedule plus a grace period.</summary>
/// <param name="Interval">The longest gap between two scheduled runs.</param>
/// <param name="Grace">The extra time allowed.</param>
public sealed record MissedThreshold(TimeSpan Interval, TimeSpan Grace)
{
    /// <summary>Seconds without a successful run after which the workload counts as missed.</summary>
    public double Seconds => Math.Round((Interval + Grace).TotalSeconds);

    /// <summary>The threshold for <paramref name="workload"/>, or <see langword="null"/> when it has no schedule.</summary>
    /// <exception cref="FormatException">The schedule is not a valid cron expression.</exception>
    public static MissedThreshold? For(WorkloadManifestEntry workload, AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(options);
        if (workload.Schedule is not { } schedule)
            return null;
        var interval = CronSchedule.MaxInterval(schedule, options.ScheduleTimeZone, options.Now);
        var grace = workload.MaxDurationSeconds is { } max ? TimeSpan.FromSeconds(max) : options.MissedGrace;
        return new MissedThreshold(interval, grace);
    }
}

/// <summary>Formats durations for alert descriptions, such as <c>1d</c>, <c>30m</c> or <c>1.5h</c>.</summary>
public static class DurationText
{
    /// <summary>Formats <paramref name="span"/> in the largest whole unit that reads naturally.</summary>
    public static string Format(TimeSpan span) =>
        span.TotalDays >= 1 && span.TotalHours % 24 == 0 ? $"{span.TotalDays:0.#}d"
        : span.TotalHours >= 1 ? $"{span.TotalHours:0.#}h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#}m"
        : $"{span.TotalSeconds:0.#}s";
}
