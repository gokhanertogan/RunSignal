using RunSignal.NewRelic;

namespace RunSignal.Tests;

public class CronScheduleTests
{
    // A Wednesday, so weekday-only schedules include a weekend gap within the horizon.
    private static readonly DateTimeOffset s_from = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("*/15 * * * *", 0.25)]           // Unix: every 15 minutes
    [InlineData("0 2 * * *", 24)]                // Unix: daily at 02:00
    [InlineData("0 9 * * 1-5", 72)]              // Unix: weekdays, Friday to Monday is the longest gap
    [InlineData("0 0 4 * * ?", 24)]              // Quartz: daily at 04:00
    [InlineData("0 0 9 ? * 2-6", 72)]            // Quartz: MON-FRI (Quartz counts SUN as 1)
    [InlineData("0 0 9 ? * MON-FRI", 72)]        // Quartz: day names
    [InlineData("0/30 * * * * ? *", 0.5 / 60)]   // Quartz with a year field: every 30 seconds
    [InlineData("0 0 1 1,4,7,10 *", 24 * 92)]    // quarterly needs the longer look ahead
    public void Max_interval_is_the_longest_gap_between_runs(string expression, double hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), CronSchedule.MaxInterval(expression, from: s_from));
    }

    [Fact]
    public void Quartz_day_of_week_occurrence_is_not_shifted()
    {
        // "6#3" is the third Friday (Quartz 6 = FRI); only the day number shifts, not the occurrence.
        var interval = CronSchedule.MaxInterval("0 0 9 ? * 6#3", from: s_from);
        Assert.InRange(interval.TotalDays, 28, 35);
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("0 2 * *")]
    [InlineData("99 2 * * *")]
    public void Invalid_expressions_are_rejected(string expression) =>
        Assert.Throws<FormatException>(() => CronSchedule.MaxInterval(expression, from: s_from));
}
