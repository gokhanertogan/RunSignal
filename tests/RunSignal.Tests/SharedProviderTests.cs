using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace RunSignal.Tests;

public class SharedProviderTests
{
    [Fact]
    public void Registries_sharing_a_meter_provider_all_report_their_gauges()
    {
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddRunSignalInstrumentation().AddInMemoryExporter(exported).Build();
        using var first = new WorkloadRegistry();
        using var second = new WorkloadRegistry();
        first.Get("probe-first", o => { o.Schedule = "0 2 * * *"; o.MaxDuration = TimeSpan.FromMinutes(1); });
        second.Get("probe-second", o => { o.Schedule = "0 2 * * *"; o.MaxDuration = TimeSpan.FromMinutes(1); });
        provider.ForceFlush();

        foreach (var name in new[] { RunSignalTelemetry.Instruments.SinceSuccess, RunSignalTelemetry.Instruments.ActiveRunAge })
        {
            var workloads = new HashSet<string>();
            foreach (var metric in exported.Where(m => m.Name == name))
                foreach (ref readonly var point in metric.GetMetricPoints())
                    foreach (var tag in point.Tags)
                        if (tag.Key == RunSignalTelemetry.Attributes.Workload && tag.Value is string w && w.StartsWith("probe-"))
                            workloads.Add(w);
            Assert.Equal(new HashSet<string> { "probe-first", "probe-second" }, workloads);
        }
    }
}
