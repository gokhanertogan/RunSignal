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
        var interval = CronSchedule.MaxInterval(schedule, ZoneOf(workload, options), options.Now);
        var grace = workload.MaxDurationSeconds is { } max ? TimeSpan.FromSeconds(max) : options.MissedGrace;
        return new MissedThreshold(interval, grace);
    }

    // The workload's own time zone wins over the tooling's default. An unknown zone is a schedule that cannot be
    // read, so callers report it the same way as a bad cron expression.
    private static TimeZoneInfo ZoneOf(WorkloadManifestEntry workload, AlertOptions options)
    {
        if (workload.TimeZone is not { } id)
            return options.ScheduleTimeZone;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new FormatException($"Time zone '{id}' is not known.", ex);
        }
    }
}

/// <summary>Formats durations for alert descriptions, such as <c>1d</c>, <c>30m</c> or <c>1.5h</c>.</summary>
public static class DurationText
{
    /// <summary>Formats <paramref name="span"/> in the largest whole unit that reads naturally.</summary>
    /// <remarks>
    /// Culture-invariant: the text goes into alert descriptions, which provisioning compares, so it must be the same
    /// on every machine (a Turkish locale would otherwise write <c>1,5h</c>).
    /// </remarks>
    public static string Format(TimeSpan span) =>
        span.TotalDays >= 1 && span.TotalHours % 24 == 0 ? FormattableString.Invariant($"{span.TotalDays:0.#}d")
        : span.TotalHours >= 1 ? FormattableString.Invariant($"{span.TotalHours:0.#}h")
        : span.TotalMinutes >= 1 ? FormattableString.Invariant($"{span.TotalMinutes:0.#}m")
        : FormattableString.Invariant($"{span.TotalSeconds:0.#}s");
}
