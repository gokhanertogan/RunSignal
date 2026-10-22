using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace RunSignal.Tests;

internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Captures RunSignal measurements for one workload name, so tests running in parallel do not see each other's.</summary>
internal sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();
    private readonly string _workload;

    public MetricRecorder(string workload)
    {
        _workload = workload;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RunSignalTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
        _listener.Start();
    }

    public IReadOnlyList<RecordedMeasurement> Of(string instrument) =>
        _measurements.Where(m => m.Instrument == instrument).ToList();

    public void CollectObservable() => _listener.RecordObservableInstruments();

    /// <summary>Waits for the first measurement of <paramref name="instrument"/>, for work that runs on a scheduler's thread.</summary>
    public async Task<RecordedMeasurement> WaitForAsync(string instrument, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var found = _measurements.FirstOrDefault(m => m.Instrument == instrument);
            if (found is not null)
                return found;
            await Task.Delay(25);
        }
        throw new TimeoutException($"No '{instrument}' measurement for workload '{_workload}' within {timeoutSeconds} s.");
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        if (Equals(dict.GetValueOrDefault(RunSignalTelemetry.Attributes.Workload), _workload))
            _measurements.Enqueue(new RecordedMeasurement(instrument.Name, value, dict));
    }
}

internal static class TestNames
{
    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
