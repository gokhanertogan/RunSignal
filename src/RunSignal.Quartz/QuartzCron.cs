using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RunSignal.Quartz;

/// <summary>Converts Quartz.NET cron expressions to the standard format RunSignal manifests use.</summary>
public static partial class QuartzCron
{
    /// <summary>
    /// Converts a Quartz cron expression (<c>sec min hour day-of-month month day-of-week [year]</c>, day of week
    /// 1-7 = SUN-SAT, <c>?</c> for "no value") to 6 fields with seconds first and day of week 0-6 = SUN-SAT, as in
    /// Unix cron. A year field is dropped. Expressions that are not 6 or 7 fields are returned unchanged.
    /// </summary>
    /// <example><c>0 0 9 ? * 2-6</c> (weekdays at 09:00) becomes <c>0 0 9 * * 1-5</c>.</example>
    public static string ToStandard(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (6 or 7))
            return expression;

        var builder = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var field = fields[i] == "?" ? "*" : fields[i];
            if (i == 5)
                field = DayOfWeekNumber().Replace(field, m => (int.Parse(m.Value, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(field).Append(' ');
        }
        return builder.ToString().TrimEnd();
    }

    // A day-of-week number: not part of a longer number, and not the occurrence after '#' (in "6#3", only 6 is a
    // day). Suffixes such as "L" in "6L" (last Friday) stay attached.
    [GeneratedRegex("(?<![#\\d])[1-7](?!\\d)")]
    private static partial Regex DayOfWeekNumber();
}
