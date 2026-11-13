using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunSignal;

/// <summary>
/// Every workload of a service and what it is expected to do: the input for alert provisioning.
/// The JSON format is defined by <c>spec/manifest.schema.json</c> and is the same for every RunSignal SDK.
/// </summary>
public sealed record WorkloadManifest
{
    /// <summary>The manifest format version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>The service the workloads belong to, matching its OpenTelemetry <c>service.name</c>.</summary>
    public required string Service { get; init; }

    /// <summary>The workloads, sorted by name so the file diffs cleanly.</summary>
    public required IReadOnlyList<WorkloadManifestEntry> Workloads { get; init; }

    /// <summary>Serializes the manifest to indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WorkloadManifestJsonContext.Relaxed.WorkloadManifest);

    /// <summary>Reads a manifest from JSON.</summary>
    public static WorkloadManifest FromJson(string json) =>
        JsonSerializer.Deserialize(json, WorkloadManifestJsonContext.Default.WorkloadManifest)
        ?? throw new JsonException("The manifest is empty.");
}

/// <summary>One workload in a <see cref="WorkloadManifest"/>. Durations are in seconds so every language reads them the same way.</summary>
public sealed record WorkloadManifestEntry
{
    /// <summary>The workload name.</summary>
    public required string Name { get; init; }

    /// <inheritdoc cref="WorkloadOptions.Schedule"/>
    public string? Schedule { get; init; }

    /// <summary>Longest acceptable run, in seconds.</summary>
    public double? MaxDurationSeconds { get; init; }

    /// <summary>Longest acceptable gap between heartbeats, in seconds.</summary>
    public double? ExpectEverySeconds { get; init; }

    /// <inheritdoc cref="WorkloadOptions.MinProcessed"/>
    public long? MinProcessed { get; init; }

    /// <inheritdoc cref="WorkloadOptions.FailureRateThreshold"/>
    public double? FailureRateThreshold { get; init; }

    /// <inheritdoc cref="WorkloadOptions.Severity"/>
    public Severity Severity { get; init; } = Severity.Warning;

    /// <inheritdoc cref="WorkloadOptions.Owner"/>
    public string? Owner { get; init; }

    /// <inheritdoc cref="WorkloadOptions.RunbookUrl"/>
    public string? RunbookUrl { get; init; }

    /// <summary>
    /// How the workload's signals arrive. <see langword="null"/> means <see cref="WorkloadReporting.Continuous"/>:
    /// a long-running process reports all the time. SDKs never set it.
    /// </summary>
    public WorkloadReporting? Reporting { get; init; }

    /// <summary>
    /// The IANA time zone the schedule runs in, such as <c>Europe/Istanbul</c>. <see langword="null"/> uses the
    /// time zone given to the alert tooling (UTC by default).
    /// </summary>
    public string? TimeZone { get; init; }

    internal static WorkloadManifestEntry From(Workload workload)
    {
        var o = workload.Options;
        return new WorkloadManifestEntry
        {
            Name = workload.Name,
            Schedule = o.Schedule,
            MaxDurationSeconds = o.MaxDuration?.TotalSeconds,
            ExpectEverySeconds = o.ExpectEvery?.TotalSeconds,
            MinProcessed = o.MinProcessed,
            FailureRateThreshold = o.FailureRateThreshold,
            Severity = o.Severity,
            Owner = o.Owner,
            RunbookUrl = o.RunbookUrl,
        };
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SeverityJsonConverter), typeof(WorkloadReportingJsonConverter)])]
[JsonSerializable(typeof(WorkloadManifest))]
internal sealed partial class WorkloadManifestJsonContext : JsonSerializerContext
{
    // Only the escapes JSON requires, as the spec asks of every SDK: "Gökhan & Billing" and URLs with a query string
    // stay readable and identical across languages instead of becoming "G\u00F6khan \u0026 Billing". The manifest is a
    // file for tools, never embedded in HTML, so the relaxed encoder is safe here.
    // Built on first use: a static initializer here could run before the generated Default exists, since the order of
    // static fields across the partial class's files is not defined. A race only builds an equal instance twice.
    private static WorkloadManifestJsonContext? s_relaxed;

    internal static WorkloadManifestJsonContext Relaxed => s_relaxed ??= CreateRelaxed();

    private static WorkloadManifestJsonContext CreateRelaxed()
    {
        var options = new JsonSerializerOptions(Default.Options)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return new WorkloadManifestJsonContext(options);
    }
}

// Writes severities as "info", "warning" and "critical", as the spec defines them.
internal sealed class SeverityJsonConverter() : JsonStringEnumConverter<Severity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

// Writes "continuous" and "per-run".
internal sealed class WorkloadReportingJsonConverter() : JsonStringEnumConverter<WorkloadReporting>(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false);

/// <summary>How a workload's signals arrive, which decides how absence is detected.</summary>
public enum WorkloadReporting
{
    /// <summary>
    /// A long-running process records the workload and reports all the time, so the <c>since_*</c> gauges show
    /// missed runs and stalls, and silence means the process is down. Every SDK-instrumented workload is like this.
    /// </summary>
    Continuous,

    /// <summary>
    /// Each run is its own short-lived process, such as a command wrapped in <c>runsignal exec</c> or a Kubernetes
    /// CronJob. Signals exist only while a run is in progress, so silence between runs is normal: a missed run is
    /// one with no successful run within the schedule plus grace.
    /// </summary>
    PerRun,
}
