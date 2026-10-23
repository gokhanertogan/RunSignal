using Cronos;

namespace RunSignal.Alerting;

/// <summary>
/// Reads the cron schedules in a manifest: 5-field Unix cron, or 6 fields with seconds first. Day of week is
/// 0-7 with 0 and 7 meaning Sunday, as in Unix cron; the Quartz integration converts Quartz expressions to this.
/// </summary>
public static class CronSchedule
{
    /// <summary>
    /// The longest gap between two consecutive runs over the next <paramref name="horizon"/> (default: 35 days, so
    /// monthly schedules are covered). A missed run is one that does not happen within this gap.
    /// </summary>
    /// <exception cref="FormatException">The expression is not a valid cron expression.</exception>
    public static TimeSpan MaxInterval(string expression, TimeZoneInfo? timeZone = null, DateTimeOffset? from = null, TimeSpan? horizon = null)
    {
        var cron = Parse(expression);
        var zone = timeZone ?? TimeZoneInfo.Utc;
        var start = from ?? DateTimeOffset.UtcNow;

        // Look a little over a month ahead; schedules that fire less often (quarterly, yearly) get a longer look.
        var max = MaxGap(cron, zone, start, start + (horizon ?? TimeSpan.FromDays(35)));
        if (max is null && horizon is null)
            max = MaxGap(cron, zone, start, start + TimeSpan.FromDays(800));
        return max ?? throw new FormatException($"Cron expression '{expression}' does not fire at least twice in the period examined.");
    }

    private static TimeSpan? MaxGap(CronExpression cron, TimeZoneInfo zone, DateTimeOffset start, DateTimeOffset end)
    {
        DateTimeOffset? previous = null;
        TimeSpan? max = null;
        foreach (var occurrence in cron.GetOccurrences(start, end, zone))
        {
            if (previous is { } p && (max is null || occurrence - p > max))
                max = occurrence - p;
            previous = occurrence;
        }
        return max;
    }

    internal static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return fields.Length switch
            {
                5 => CronExpression.Parse(expression, CronFormat.Standard),
                6 => CronExpression.Parse(expression, CronFormat.IncludeSeconds),
                _ => throw new FormatException($"Cron expression '{expression}' has {fields.Length} fields; expected 5, or 6 with seconds first."),
            };
        }
        catch (CronFormatException ex)
        {
            throw new FormatException($"Cron expression '{expression}' is invalid: {ex.Message}", ex);
        }
    }
}
