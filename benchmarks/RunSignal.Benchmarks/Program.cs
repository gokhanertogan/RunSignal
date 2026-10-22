using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using RunSignal;

BenchmarkSwitcher.FromAssembly(typeof(WorkloadBenchmarks).Assembly).Run(args);

/// <summary>
/// Cost of recording signals on the hot path. "Listening" simulates an exporter such as OpenTelemetry
/// collecting the measurements; without a listener the instruments are disabled.
/// </summary>
[MemoryDiagnoser]
public class WorkloadBenchmarks
{
    private WorkloadRegistry _registry = null!;
    private Workload _workload = null!;
    private MeterListener? _listener;

    [Params(false, true)]
    public bool Listening { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _registry = new WorkloadRegistry();
        _workload = _registry.Get("benchmark");
        if (Listening)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == RunSignalTelemetry.MeterName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            _listener.Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _registry.Dispose();
    }

    [Benchmark]
    public void StartAndSucceed() => _workload.Start().Succeed();

    [Benchmark]
    public void StartAndFailWithCode() => _workload.Start().Fail("statement_missing");

    [Benchmark]
    public void Processed() => _workload.Processed();

    [Benchmark]
    public void Beat() => _workload.Beat();

    [Benchmark]
    public Task RunAsync() => _workload.RunAsync(static _ => Task.CompletedTask);
}
