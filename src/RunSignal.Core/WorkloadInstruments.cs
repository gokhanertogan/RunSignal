using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RunSignal;

internal sealed class WorkloadInstruments
{
    internal static readonly ActivitySource ActivitySource = new(RunSignalTelemetry.ActivitySourceName, Version);

    internal static string Version => typeof(WorkloadInstruments).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public WorkloadInstruments(Meter meter, Func<IEnumerable<Measurement<long>>> observeLastSuccess)
    {
        Runs = meter.CreateCounter<long>(RunSignalTelemetry.Instruments.Runs, "{run}", "Completed runs of a workload.");
        Duration = meter.CreateHistogram<double>(RunSignalTelemetry.Instruments.Duration, "s", "Duration of workload runs.");
        Items = meter.CreateCounter<long>(RunSignalTelemetry.Instruments.Items, "{item}", "Items handled by workload runs.");
        Heartbeats = meter.CreateCounter<long>(RunSignalTelemetry.Instruments.Heartbeats, "{heartbeat}", "Heartbeats of long-running workloads.");
        meter.CreateObservableGauge(RunSignalTelemetry.Instruments.LastSuccess, observeLastSuccess, "s", "Unix time of the last successful run of a workload.");
    }

    public Counter<long> Runs { get; }

    public Histogram<double> Duration { get; }

    public Counter<long> Items { get; }

    public Counter<long> Heartbeats { get; }
}
