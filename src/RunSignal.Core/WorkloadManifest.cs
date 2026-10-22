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
    public string ToJson() => JsonSerializer.Serialize(this, WorkloadManifestJsonContext.Default.WorkloadManifest);

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
        };
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SeverityJsonConverter)])]
[JsonSerializable(typeof(WorkloadManifest))]
internal sealed partial class WorkloadManifestJsonContext : JsonSerializerContext;

// Writes severities as "info", "warning" and "critical", as the spec defines them.
internal sealed class SeverityJsonConverter() : JsonStringEnumConverter<Severity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);
