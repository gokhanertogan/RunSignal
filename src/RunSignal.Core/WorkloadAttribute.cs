using System.Globalization;

namespace RunSignal;

/// <summary>
/// Declares a job class or method as a workload and sets its expectations. Scheduler integrations
/// (RunSignal.Hangfire, RunSignal.Quartz) read it; without it they name the workload after the job.
/// </summary>
/// <example>
/// <code>
/// [Workload("nightly-invoice", Schedule = "0 2 * * *", MaxDuration = "30m", MinProcessed = 100, Severity = Severity.Critical)]
/// public async Task GenerateInvoices() { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class WorkloadAttribute : Attribute
{
    /// <summary>Declares a workload, optionally with an explicit name.</summary>
    /// <param name="name">The workload name; when omitted, the integration derives one from the job.</param>
    public WorkloadAttribute(string? name = null) => Name = name;

    /// <summary>The workload name, or <see langword="null"/> to derive one from the job.</summary>
    public string? Name { get; }

    /// <inheritdoc cref="WorkloadOptions.Schedule"/>
    /// <remarks>When omitted, integrations use the job's own schedule (a recurring job's cron or a cron trigger).</remarks>
    public string? Schedule { get; set; }

    /// <summary>Longest acceptable run, such as <c>"90s"</c>, <c>"30m"</c>, <c>"2h"</c> or <c>"00:30:00"</c>. Produces the <c>overrun</c> alert.</summary>
    public string? MaxDuration { get; set; }

    /// <summary>Longest acceptable gap between heartbeats, in the same format as <see cref="MaxDuration"/>. Produces the <c>stalled</c> alert.</summary>
    public string? ExpectEvery { get; set; }

    /// <summary>Fewest items a run should process; 0 means no expectation. Produces the <c>low-output</c> alert.</summary>
    public long MinProcessed { get; set; }

    /// <summary>Highest acceptable share (0 to 1) of failed runs or items; 0 means no expectation. Produces the <c>failure-rate</c> alert.</summary>
    public double FailureRateThreshold { get; set; }

    /// <inheritdoc cref="WorkloadOptions.Severity"/>
    public Severity Severity { get; set; } = Severity.Warning;

    /// <summary>Copies the expectations set on this attribute to <paramref name="options"/>.</summary>
    /// <exception cref="FormatException"><see cref="MaxDuration"/> or <see cref="ExpectEvery"/> is not a valid duration.</exception>
    public void ApplyTo(WorkloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Schedule is not null)
            options.Schedule = Schedule;
        if (MaxDuration is not null)
            options.MaxDuration = ParseDuration(MaxDuration, nameof(MaxDuration));
        if (ExpectEvery is not null)
            options.ExpectEvery = ParseDuration(ExpectEvery, nameof(ExpectEvery));
        if (MinProcessed > 0)
            options.MinProcessed = MinProcessed;
        if (FailureRateThreshold > 0)
            options.FailureRateThreshold = FailureRateThreshold;
        options.Severity = Severity;
    }

    internal static TimeSpan ParseDuration(string value, string property)
    {
        var text = value.Trim();
        if (text.Length >= 2 && char.IsAsciiLetterLower(text[^1]) &&
            double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            switch (text[^1])
            {
                case 's': return TimeSpan.FromSeconds(amount);
                case 'm': return TimeSpan.FromMinutes(amount);
                case 'h': return TimeSpan.FromHours(amount);
                case 'd': return TimeSpan.FromDays(amount);
            }
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
            return span;

        throw new FormatException($"{property} '{value}' is not a duration. Use a number with s, m, h or d (such as \"30m\"), or hh:mm:ss.");
    }
}
