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

        _instruments = new WorkloadInstruments(_meter, ObserveSinceSuccess, ObserveSinceHeartbeat);
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

    /// <summary>
    /// Returns the workload called <paramref name="name"/>, creating it with <paramref name="configure"/> if it
    /// does not exist yet. Unlike <see cref="Get"/>, an existing workload is returned as is: options declared
    /// elsewhere (for example with <c>AddWorkload</c>) take precedence. Integrations use this to name workloads
    /// after jobs.
    /// </summary>
    public Workload GetOrAdd(string name, Action<WorkloadOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_workloads.TryGetValue(name, out var existing))
            return existing;

        ValidateName(name);
        var options = new WorkloadOptions();
        configure(options);
        return _workloads.GetOrAdd(name, new Workload(name, options, _instruments));
    }

    /// <summary>Describes every workload created so far, for alert provisioning.</summary>
    /// <param name="service">The service name, matching the OpenTelemetry <c>service.name</c> the signals are exported with.</param>
    public WorkloadManifest CreateManifest(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return new WorkloadManifest
        {
            Service = service,
            Workloads = _workloads.Values
                .OrderBy(w => w.Name, StringComparer.Ordinal)
                .Select(WorkloadManifestEntry.From)
                .ToArray(),
        };
    }

    /// <summary>
    /// Turns an arbitrary job identifier, such as <c>InvoiceJobs.GenerateInvoices</c> or <c>Reports/Daily Sales</c>,
    /// into a valid workload name such as <c>invoice-jobs.generate-invoices</c> or <c>reports-daily-sales</c>.
    /// </summary>
    public static string ToWorkloadName(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        identifier = RemoveDiacritics(identifier);
        var builder = new System.Text.StringBuilder(identifier.Length + 8);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (char.IsAsciiLetterUpper(c))
            {
                // Break camel case: "GenerateInvoices" -> "generate-invoices", "HTTPSync" -> "http-sync".
                var previous = i > 0 ? identifier[i - 1] : '\0';
                var next = i + 1 < identifier.Length ? identifier[i + 1] : '\0';
                if (builder.Length > 0 && builder[^1] is not ('-' or '.' or '_') &&
                    (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next))))
                    builder.Append('-');
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var name = builder.ToString().Trim('-', '.', '_');
        if (name.Length > 128)
            name = name[..128].TrimEnd('-', '.', '_');
        if (name.Length == 0)
            throw new ArgumentException($"'{identifier}' contains no letters or digits to name a workload after.", nameof(identifier));
        return name;
    }

    // "e-Arşiv gönderimi" -> "e-Arsiv gonderimi", so names stay readable instead of losing letters.
    private static string RemoveDiacritics(string value)
    {
        if (System.Text.Ascii.IsValid(value))
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            // Letters that do not decompose into a base letter and a mark.
            switch (c)
            {
                case 'ı': builder.Append('i'); break;
                case 'ß': builder.Append("ss"); break;
                case 'ø': builder.Append('o'); break;
                case 'Ø': builder.Append('O'); break;
                case 'æ': builder.Append("ae"); break;
                case 'Æ': builder.Append("Ae"); break;
                case 'đ': builder.Append('d'); break;
                case 'Đ': builder.Append('D'); break;
                case 'ł': builder.Append('l'); break;
                case 'Ł': builder.Append('L'); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }

    private IEnumerable<Measurement<double>> ObserveSinceSuccess()
    {
        foreach (var workload in _workloads.Values)
        {
            if (workload.TryGetSecondsSinceSuccess(out var seconds))
                yield return new Measurement<double>(seconds, new KeyValuePair<string, object?>(RunSignalTelemetry.Attributes.Workload, workload.Name));
        }
    }

    private IEnumerable<Measurement<double>> ObserveSinceHeartbeat()
    {
        foreach (var workload in _workloads.Values)
        {
            if (workload.TryGetSecondsSinceHeartbeat(out var seconds))
                yield return new Measurement<double>(seconds, new KeyValuePair<string, object?>(RunSignalTelemetry.Attributes.Workload, workload.Name));
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
