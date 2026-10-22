using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace RunSignal;

/// <summary>
/// Creates and holds the workloads of an application. Use <see cref="Shared"/>, or the instance registered by
/// <c>AddRunSignal()</c> when you use dependency injection.
/// </summary>
public sealed partial class WorkloadRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, Workload> _workloads = new(StringComparer.Ordinal);
    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly WorkloadInstruments _instruments;

    /// <summary>Creates a registry that records on its own <see cref="Meter"/>, or on one from <paramref name="meterFactory"/>.</summary>
    public WorkloadRegistry(IMeterFactory? meterFactory = null)
    {
        if (meterFactory is null)
        {
            _meter = new Meter(RunSignalTelemetry.MeterName, WorkloadInstruments.Version);
            _ownsMeter = true;
        }
        else
        {
            _meter = meterFactory.Create(RunSignalTelemetry.MeterName, WorkloadInstruments.Version);
        }

        _instruments = new WorkloadInstruments(_meter, ObserveLastSuccess);
    }

    /// <summary>A process-wide registry for code that does not use dependency injection.</summary>
    public static WorkloadRegistry Shared { get; } = new();

    /// <summary>Every workload created so far.</summary>
    public IReadOnlyCollection<Workload> Workloads => _workloads.Values.ToArray();

    /// <summary>
    /// Returns the workload called <paramref name="name"/>, creating it on first use.
    /// Call this once and keep the result rather than calling it on every run.
    /// </summary>
    /// <param name="name">A stable, low-cardinality name such as <c>nightly-invoice</c> (lowercase letters, digits, '.', '_' and '-').</param>
    /// <param name="configure">Sets the workload's expectations. Only allowed when the workload is created.</param>
    /// <exception cref="InvalidOperationException"><paramref name="configure"/> is given but the workload already exists.</exception>
    public Workload Get(string name, Action<WorkloadOptions>? configure = null)
    {
        if (_workloads.TryGetValue(name, out var existing))
        {
            if (configure is not null)
                throw new InvalidOperationException($"Workload '{name}' is already registered; configure it only where it is first created.");
            return existing;
        }

        ValidateName(name);
        var options = new WorkloadOptions();
        configure?.Invoke(options);
        var created = new Workload(name, options, _instruments);
        if (!_workloads.TryAdd(name, created))
        {
            if (configure is not null)
                throw new InvalidOperationException($"Workload '{name}' is already registered; configure it only where it is first created.");
            return _workloads[name];
        }

        return created;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }

    private IEnumerable<Measurement<long>> ObserveLastSuccess()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var workload in _workloads.Values)
        {
            if (workload.TryGetLastSuccessUnixSeconds(now, out var lastSuccess))
                yield return new Measurement<long>(lastSuccess, new KeyValuePair<string, object?>(RunSignalTelemetry.Attributes.Workload, workload.Name));
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException($"Workload name '{name}' is invalid. Use 1-128 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.", nameof(name));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex NamePattern();
}
