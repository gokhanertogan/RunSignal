namespace RunSignal;

/// <summary>
/// Names of the meter, activity source, instruments and attributes RunSignal emits.
/// They are identical in every RunSignal SDK (see <c>spec/README.md</c>), so alert rules work across languages.
/// </summary>
public static class RunSignalTelemetry
{
    /// <summary>Name of the <see cref="System.Diagnostics.Metrics.Meter"/> RunSignal records metrics on.</summary>
    public const string MeterName = "RunSignal";

    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> RunSignal records run spans on.</summary>
    public const string ActivitySourceName = "RunSignal";

    /// <summary>Instrument names.</summary>
    public static class Instruments
    {
        /// <summary>Counter of completed runs.</summary>
        public const string Runs = "runsignal.workload.runs";

        /// <summary>Histogram of run durations, in seconds.</summary>
        public const string Duration = "runsignal.workload.duration";

        /// <summary>Counter of items handled by runs.</summary>
        public const string Items = "runsignal.workload.items";

        /// <summary>Counter of heartbeats.</summary>
        public const string Heartbeats = "runsignal.workload.heartbeats";

        /// <summary>Gauge of the Unix time, in seconds, of the last successful run.</summary>
        public const string LastSuccess = "runsignal.workload.last_success";
    }

    /// <summary>Attribute (tag) names.</summary>
    public static class Attributes
    {
        /// <summary>The workload name.</summary>
        public const string Workload = "runsignal.workload";

        /// <summary>The run outcome: <c>success</c>, <c>failure</c> or <c>cancelled</c>.</summary>
        public const string Outcome = "runsignal.outcome";

        /// <summary>The item result: <c>processed</c> or <c>failed</c>.</summary>
        public const string ItemResult = "runsignal.item.result";

        /// <summary>The low-cardinality error type of a failed run.</summary>
        public const string ErrorType = "error.type";
    }

    /// <summary>
    /// Recommended histogram bucket boundaries, in seconds, for <see cref="Instruments.Duration"/>.
    /// Background work runs from milliseconds to hours, while the OpenTelemetry defaults stop at 10 seconds.
    /// </summary>
    public static double[] DurationBuckets =>
        [0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800];
}
