using System.Text;
using System.Text.RegularExpressions;
using Cronos;

namespace RunSignal.NewRelic;

/// <summary>Reads the cron schedules in a manifest: 5-field Unix cron, or 6/7-field Quartz cron with seconds.</summary>
public static partial class CronSchedule
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
                6 or 7 => CronExpression.Parse(FromQuartz(fields), CronFormat.IncludeSeconds),
                _ => throw new FormatException($"Cron expression '{expression}' has {fields.Length} fields; expected 5 (Unix) or 6-7 (Quartz)."),
            };
        }
        catch (CronFormatException ex)
        {
            throw new FormatException($"Cron expression '{expression}' is invalid: {ex.Message}", ex);
        }
    }

    // Quartz: sec min hour day-of-month month day-of-week [year], with '?' for "no value" and day-of-week 1-7 = SUN-SAT.
    // Cronos:  sec min hour day-of-month month day-of-week, with day-of-week 0-6 = SUN-SAT. The year field is dropped.
    private static string FromQuartz(string[] fields)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var field = fields[i] == "?" ? "*" : fields[i];
            if (i == 5)
                field = DayOfWeekNumber().Replace(field, m => (int.Parse(m.Value) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(field).Append(' ');
        }
        return builder.ToString().TrimEnd();
    }

    // A day-of-week number, but not the occurrence after '#' (in "6#3", only 6 is a day).
    [GeneratedRegex("(?<!#)\\b[1-7]\\b")]
    private static partial Regex DayOfWeekNumber();
}
