using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RunSignal.Cli;

/// <summary>
/// Turns Kubernetes CronJobs (the JSON of <c>kubectl get cronjobs -o json</c>) into a manifest of per-run workloads,
/// for jobs whose containers run their command through <c>runsignal exec</c>.
/// </summary>
internal static partial class KubernetesCronJobs
{
    /// <summary>Annotations that override or add to what the CronJob itself says.</summary>
    internal static class Annotations
    {
        public const string Prefix = "runsignal.io/";
        public const string Workload = Prefix + "workload";
        public const string Ignore = Prefix + "ignore";
        public const string Severity = Prefix + "severity";
        public const string MaxDuration = Prefix + "max-duration";
        public const string MinProcessed = Prefix + "min-processed";
        public const string FailureRate = Prefix + "failure-rate";
        public const string Owner = Prefix + "owner";
        public const string RunbookUrl = Prefix + "runbook-url";
    }

    // Kubernetes accepts these macros; the manifest takes plain cron.
    private static readonly Dictionary<string, string> s_macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
        ["@monthly"] = "0 0 1 * *",
        ["@weekly"] = "0 0 * * 0",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@hourly"] = "0 * * * *",
    };

    /// <summary>Builds the manifest. <paramref name="warnings"/> lists CronJobs that were skipped, and why.</summary>
    /// <exception cref="FormatException">The JSON is not a CronJob list, an annotation is invalid, or two CronJobs map to one workload.</exception>
    public static WorkloadManifest ToManifest(JsonElement root, string service, out IReadOnlyList<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        var notes = new List<string>();
        var items = root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : root.TryGetProperty("kind", out var kind) && kind.GetString() == "CronJob"
                ? [root]
                : throw new FormatException("The input is not a CronJob or a list of CronJobs (kubectl get cronjobs -o json).");

        var workloads = new Dictionary<string, (WorkloadManifestEntry Entry, string Source)>(StringComparer.Ordinal);
        foreach (var cronJob in items)
        {
            var metadata = cronJob.GetProperty("metadata");
            var name = metadata.GetProperty("name").GetString()!;
            var ns = metadata.TryGetProperty("namespace", out var n) ? n.GetString() : null;
            var source = ns is null ? name : $"{ns}/{name}";
            var annotations = ReadAnnotations(metadata);
            var spec = cronJob.GetProperty("spec");

            if (annotations.TryGetValue(Annotations.Ignore, out var ignore) && string.Equals(ignore, "true", StringComparison.OrdinalIgnoreCase))
                continue;
            if (spec.TryGetProperty("suspend", out var suspend) && suspend.ValueKind == JsonValueKind.True)
            {
                notes.Add($"{source}: skipped, because it is suspended.");
                continue;
            }

            var (schedule, scheduleZone) = NormalizeSchedule(spec.GetProperty("schedule").GetString()!);
            if (schedule is null)
            {
                notes.Add($"{source}: skipped, because its schedule '{spec.GetProperty("schedule").GetString()}' is not a cron expression RunSignal can read.");
                continue;
            }

            var entry = new WorkloadManifestEntry
            {
                Name = WorkloadName(annotations, name, source),
                Schedule = schedule,
                TimeZone = spec.TryGetProperty("timeZone", out var zone) && zone.GetString() is { Length: > 0 } z ? z : scheduleZone,
                MaxDurationSeconds = MaxDuration(annotations, spec, source),
                MinProcessed = annotations.TryGetValue(Annotations.MinProcessed, out var min) ? ParseMinProcessed(min, source) : null,
                FailureRateThreshold = annotations.TryGetValue(Annotations.FailureRate, out var rate) ? ParseRate(rate, source) : null,
                Severity = annotations.TryGetValue(Annotations.Severity, out var severity) ? ParseSeverity(severity, source) : Severity.Warning,
                Reporting = WorkloadReporting.PerRun,
                Owner = annotations.TryGetValue(Annotations.Owner, out var owner) ? ParseOwner(owner, source) : null,
                RunbookUrl = annotations.TryGetValue(Annotations.RunbookUrl, out var runbook) ? ParseRunbook(runbook, source) : null,
            };

            if (workloads.TryGetValue(entry.Name, out var existing))
                throw new FormatException($"{existing.Source} and {source} both become workload '{entry.Name}'. Give one of them a {Annotations.Workload} annotation.");
            workloads[entry.Name] = (entry, source);
        }

        warnings = notes;
        return new WorkloadManifest
        {
            Service = service,
            Workloads = workloads.Values.Select(w => w.Entry).OrderBy(w => w.Name, StringComparer.Ordinal).ToArray(),
        };
    }

    // "CRON_TZ=Europe/Istanbul 0 2 * * *" and "@daily" become plain cron, with the zone returned separately.
    // Returns a null schedule for what the manifest cannot express, such as "@every 5m".
    internal static (string? Schedule, string? TimeZone) NormalizeSchedule(string value)
    {
        var text = value.Trim();
        string? zone = null;
        var match = TimeZonePrefix().Match(text);
        if (match.Success)
        {
            zone = match.Groups["zone"].Value;
            text = text[match.Length..].Trim();
        }

        if (text.StartsWith('@'))
            return s_macros.TryGetValue(text, out var cron) ? (cron, zone) : (null, zone);
        return (string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), zone);
    }

    private static Dictionary<string, string> ReadAnnotations(JsonElement metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in annotations.EnumerateObject())
            {
                if (property.Name.StartsWith(Annotations.Prefix, StringComparison.Ordinal) && property.Value.GetString() is { } text)
                    result[property.Name] = text.Trim();
            }
        }
        return result;
    }

    private static string WorkloadName(Dictionary<string, string> annotations, string cronJobName, string source)
    {
        if (!annotations.TryGetValue(Annotations.Workload, out var name))
            return WorkloadRegistry.ToWorkloadName(cronJobName);
        if (!WorkloadNamePattern().IsMatch(name))
            throw new FormatException($"{source}: {Annotations.Workload} '{name}' is not a valid workload name (lowercase letters, digits, '.', '_' and '-').");
        return name;
    }

    // The annotation wins; otherwise the job's own deadline is the longest acceptable run.
    private static double? MaxDuration(Dictionary<string, string> annotations, JsonElement spec, string source)
    {
        if (annotations.TryGetValue(Annotations.MaxDuration, out var text))
        {
            try
            {
                return WorkloadAttribute.ParseDuration(text, Annotations.MaxDuration).TotalSeconds;
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{source}: {ex.Message}", ex);
            }
        }

        return spec.TryGetProperty("jobTemplate", out var template) &&
               template.TryGetProperty("spec", out var jobSpec) &&
               jobSpec.TryGetProperty("activeDeadlineSeconds", out var deadline) &&
               deadline.ValueKind == JsonValueKind.Number
            ? deadline.GetDouble()
            : null;
    }

    private static long ParseMinProcessed(string text, string source) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1
            ? value
            : throw new FormatException($"{source}: {Annotations.MinProcessed} '{text}' is not a whole number of at least 1.");

    private static double ParseRate(string text, string source) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 1
            ? value
            : throw new FormatException($"{source}: {Annotations.FailureRate} '{text}' is not a number above 0 and at most 1, such as 0.2.");

    private static Severity ParseSeverity(string text, string source) =>
        Enum.TryParse<Severity>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(text, out _)
            ? value
            : throw new FormatException($"{source}: {Annotations.Severity} '{text}' is not info, warning or critical.");

    private static string ParseOwner(string text, string source) =>
        text.Length is > 0 and <= 128
            ? text
            : throw new FormatException($"{source}: {Annotations.Owner} must be 1 to 128 characters.");

    private static string ParseRunbook(string text, string source) =>
        WorkloadOptions.IsWebUrl(text)
            ? text
            : throw new FormatException($"{source}: {Annotations.RunbookUrl} '{text}' is not an absolute http or https URL.");

    [GeneratedRegex(@"^(CRON_TZ|TZ)=(?<zone>\S+)\s+")]
    private static partial Regex TimeZonePrefix();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex WorkloadNamePattern();
}
