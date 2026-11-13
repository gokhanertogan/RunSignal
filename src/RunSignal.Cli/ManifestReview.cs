using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RunSignal.Alerting;
using RunSignal.NewRelic;
using RunSignal.Prometheus;

namespace RunSignal.Cli;

/// <summary>What <c>runsignal validate</c> found in a manifest.</summary>
/// <param name="IsError">Errors make the manifest unusable or ambiguous; warnings are expectations that produce no alert.</param>
/// <param name="Source">The manifest file.</param>
/// <param name="Message">The finding, starting with the workload it concerns.</param>
internal sealed record Finding(bool IsError, string Source, string Message);

/// <summary>One alert that a manifest change adds, removes or changes.</summary>
internal sealed record AlertChange(string Service, string Workload, string Alert, string Change, string? Before, string? After);

/// <summary>
/// Checks manifests and compares two versions of them, offline. Both use the same planners as <c>provision</c> and
/// <c>export prometheus</c>, so what they report is what those commands would do.
/// </summary>
internal static partial class ManifestReview
{
    public static IReadOnlyList<Finding> Validate(IReadOnlyList<(string Source, WorkloadManifest Manifest)> manifests, AlertPlanOptions options)
    {
        var findings = new List<Finding>();
        var owners = new Dictionary<(string Service, string Workload), string>();
        foreach (var (source, manifest) in manifests)
        {
            void Error(string message) => findings.Add(new Finding(true, source, message));

            if (string.IsNullOrWhiteSpace(manifest.Service))
                Error("the manifest has no service.");
            foreach (var w in manifest.Workloads)
            {
                if (!NamePattern().IsMatch(w.Name))
                    Error($"{w.Name}: not a valid workload name (1-128 lowercase letters, digits, '.', '_' or '-').");
                if (owners.TryGetValue((manifest.Service, w.Name), out var first))
                    Error($"{w.Name}: declared twice for service '{manifest.Service}'{(first == source ? "" : $" (also in {first})")}.");
                else
                    owners[(manifest.Service, w.Name)] = source;

                if (w.MaxDurationSeconds is <= 0)
                    Error($"{w.Name}: maxDurationSeconds must be above 0.");
                if (w.ExpectEverySeconds is <= 0)
                    Error($"{w.Name}: expectEverySeconds must be above 0.");
                if (w.MinProcessed is < 1)
                    Error($"{w.Name}: minProcessed must be at least 1.");
                if (w.FailureRateThreshold is { } rate && rate is <= 0 or > 1)
                    Error($"{w.Name}: failureRateThreshold must be above 0 and at most 1.");
                if (w.Schedule is not null)
                {
                    try
                    {
                        _ = MissedThreshold.For(w, ToAlertOptions(options));
                    }
                    catch (FormatException ex)
                    {
                        Error($"{w.Name}: {ex.Message}");
                    }
                }
            }

            // Warnings: expectations a backend cannot turn into an alert. Schedule errors are already reported.
            var newRelic = AlertPlanner.Plan(manifest, options).Warnings;
            PrometheusRules.Generate([manifest], ToAlertOptions(options), out var prometheus);
            foreach (var warning in newRelic.Union(prometheus, StringComparer.Ordinal))
            {
                if (warning.Contains("because the schedule could not be read", StringComparison.Ordinal))
                    continue;
                var backend = newRelic.Contains(warning) == prometheus.Contains(warning) ? "" : newRelic.Contains(warning) ? " (New Relic)" : " (Prometheus)";
                findings.Add(new Finding(false, source, warning + backend));
            }
        }
        return findings;
    }

    /// <summary>The alerts that change between <paramref name="before"/> and <paramref name="after"/>, by service and workload.</summary>
    public static IReadOnlyList<AlertChange> Compare(IEnumerable<WorkloadManifest> before, IEnumerable<WorkloadManifest> after, AlertPlanOptions options)
    {
        var old = Alerts(before, options);
        var @new = Alerts(after, options);
        var changes = new List<AlertChange>();
        foreach (var key in old.Keys.Union(@new.Keys).Order())
        {
            var had = old.GetValueOrDefault(key);
            var has = @new.GetValueOrDefault(key);
            var (service, workload, alert) = key;
            if (had is null)
                changes.Add(new AlertChange(service, workload, alert, "added", null, has));
            else if (has is null)
                changes.Add(new AlertChange(service, workload, alert, "removed", had, null));
            else if (had != has)
                changes.Add(new AlertChange(service, workload, alert, "changed", had, has));
        }
        return changes;
    }

    public static string Markdown(IReadOnlyList<AlertChange> changes, IReadOnlyList<Finding> findings)
    {
        var text = new StringBuilder("### RunSignal alerts\n\n");
        if (changes.Count == 0)
        {
            text.Append("No alert changes.\n");
        }
        else
        {
            text.Append("| Service | Workload | Alert | Change | Before | After |\n|---|---|---|---|---|---|\n");
            foreach (var c in changes)
                text.Append(CultureInfo.InvariantCulture, $"| {Cell(c.Service)} | `{c.Workload}` | {c.Alert} | {Symbol(c.Change)} {c.Change} | {Cell(c.Before ?? "")} | {Cell(c.After ?? "")} |\n");
        }

        return text.Append('\n').Append(FindingsMarkdown(findings)).ToString();
    }

    public static string FindingsMarkdown(IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
            return "Manifests are valid.\n";
        var text = new StringBuilder();
        foreach (var f in findings.OrderBy(f => !f.IsError))
            text.Append(CultureInfo.InvariantCulture, $"- {(f.IsError ? ":x: **error**" : ":warning:")} `{f.Source}`: {Cell(f.Message)}\n");
        return text.ToString();
    }

    private static string Symbol(string change) => change switch { "added" => "+", "removed" => "−", _ => "~" };

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    // Every alert, keyed by service, workload and kind, described as a reader would: what fires it and how urgent it is.
    private static Dictionary<(string Service, string Workload, string Alert), string> Alerts(IEnumerable<WorkloadManifest> manifests, AlertPlanOptions options)
    {
        var alerts = new Dictionary<(string, string, string), string>();
        foreach (var manifest in manifests)
        {
            foreach (var policy in AlertPlanner.Plan(manifest, options).Policies)
            {
                foreach (var c in policy.Conditions)
                    alerts[(manifest.Service, c.Workload, Name(c.Kind))] = $"{Describe(c)}, {policy.Severity.ToString().ToLowerInvariant()}";
            }
        }
        return alerts;
    }

    private static string Name(AlertKind kind) => kind switch
    {
        AlertKind.LowOutput => "low output",
        AlertKind.FailureRate => "failure rate",
        AlertKind.ItemFailureRate => "item failure rate",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Describe(AlertCondition c)
    {
        string Seconds(double s) => DurationText.Format(TimeSpan.FromSeconds(s));
        string Percent(double r) => (r * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        return c.Kind switch
        {
            // A per-run missed alert is armed by loss of signal; its threshold never fires.
            AlertKind.Missed when c.LossOfSignalSeconds is { } after && !c.Above => $"no success for {Seconds(after)}",
            AlertKind.Missed => $"no success for {Seconds(c.Threshold)}",
            AlertKind.Stalled => $"no heartbeat for {Seconds(c.Threshold)}",
            AlertKind.Overrun => $"a run takes longer than {Seconds(c.Threshold)}",
            AlertKind.Hung => $"a run is still running after {Seconds(c.Threshold)}",
            AlertKind.LowOutput => $"fewer than {c.Threshold.ToString(CultureInfo.InvariantCulture)} items per run",
            AlertKind.Failed => "any failed run",
            AlertKind.FailureRate => $"over {Percent(c.Threshold)} of runs fail",
            AlertKind.ItemFailureRate => $"over {Percent(c.Threshold)} of items fail",
            _ => c.Description,
        };
    }

    private static AlertOptions ToAlertOptions(AlertPlanOptions o) => new()
    {
        MissedGrace = o.MissedGrace,
        NotReportingAfter = o.NotReportingAfter,
        ScheduleTimeZone = o.ScheduleTimeZone,
        Now = o.Now,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex NamePattern();
}
