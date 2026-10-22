using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace RunSignal;

/// <summary>Adds RunSignal to the OpenTelemetry SDK.</summary>
public static class RunSignalOpenTelemetryExtensions
{
    /// <summary>
    /// Collects RunSignal's workload metrics, with duration buckets from 10 ms to 8 hours
    /// (see <see cref="RunSignalTelemetry.DurationBuckets"/>).
    /// </summary>
    public static MeterProviderBuilder AddRunSignalInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddMeter(RunSignalTelemetry.MeterName)
            .AddView(RunSignalTelemetry.Instruments.Duration, new ExplicitBucketHistogramConfiguration
            {
                Boundaries = RunSignalTelemetry.DurationBuckets,
            });
    }

    /// <summary>Records one span per workload run.</summary>
    public static TracerProviderBuilder AddRunSignalInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(RunSignalTelemetry.ActivitySourceName);
    }
}
