using RunSignal.Cli;
using RunSignal.NewRelic;

namespace RunSignal.Tests;

// runsignal validate and runsignal compare: offline checks for CI and pull request comments.
public sealed class ReviewTests : IDisposable
{
    private static readonly AlertPlanOptions s_options = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    private readonly string _dir = Directory.CreateTempSubdirectory("runsignal-review-").FullName;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static WorkloadManifest Manifest(params WorkloadManifestEntry[] workloads) => new() { Service = "billing", Workloads = workloads };

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Structural_problems_are_errors_and_missing_alerts_are_warnings()
    {
        var findings = ManifestReview.Validate(
        [
            ("a.json", Manifest(
                new WorkloadManifestEntry { Name = "Bad Name", Schedule = "0 2 * * *" },
                new WorkloadManifestEntry { Name = "late", Schedule = "0 25 * * *" },
                new WorkloadManifestEntry { Name = "zone", Schedule = "0 2 * * *", TimeZone = "Mars/Olympus" },
                new WorkloadManifestEntry { Name = "rate", FailureRateThreshold = 1.5 },
                new WorkloadManifestEntry { Name = "idle" })),
            ("b.json", Manifest(new WorkloadManifestEntry { Name = "late", Schedule = "0 3 * * *" })),
        ], s_options);

        var errors = findings.Where(f => f.IsError).Select(f => f.Message).ToList();
        Assert.Contains(errors, e => e.StartsWith("Bad Name: not a valid workload name", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("late: Cron expression '0 25 * * *' is invalid", StringComparison.Ordinal));
        Assert.Contains(errors, e => e == "zone: Time zone 'Mars/Olympus' is not known.");
        Assert.Contains(errors, e => e == "rate: failureRateThreshold must be above 0 and at most 1.");
        Assert.Contains(findings, f => f is { IsError: true, Source: "b.json", Message: "late: declared twice for service 'billing' (also in a.json)." });
        // A schedule that cannot be read is reported once, as an error, not again as a planner warning.
        Assert.DoesNotContain(findings, f => !f.IsError && f.Message.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains(findings, f => !f.IsError && f.Message.StartsWith("idle: no alerts", StringComparison.Ordinal));
    }

    [Fact]
    public void A_warning_from_one_backend_says_which()
    {
        var findings = ManifestReview.Validate(
            [("a.json", Manifest(new WorkloadManifestEntry { Name = "weekly", Schedule = "0 6 * * 1", Reporting = WorkloadReporting.PerRun }))], s_options);

        // Prometheus alerts on a weekly per-run job; New Relic's loss of signal cannot wait a week.
        var warning = Assert.Single(findings);
        Assert.False(warning.IsError);
        Assert.EndsWith("(New Relic)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_reports_added_removed_and_changed_alerts_in_reader_terms()
    {
        var before = Manifest(
            new WorkloadManifestEntry { Name = "invoice", Schedule = "0 2 * * *", MaxDurationSeconds = 1800, Severity = Severity.Critical },
            new WorkloadManifestEntry { Name = "old", ExpectEverySeconds = 300 });
        var after = Manifest(
            new WorkloadManifestEntry { Name = "invoice", Schedule = "0 2 * * *", MaxDurationSeconds = 2700, Severity = Severity.Critical },
            new WorkloadManifestEntry { Name = "sync", ExpectEverySeconds = 60, Severity = Severity.Critical });

        var changes = ManifestReview.Compare([before], [after], s_options);

        Assert.Contains(changes, c => c is { Workload: "invoice", Alert: "overrun", Change: "changed", Before: "a run takes longer than 30m, critical", After: "a run takes longer than 45m, critical" });
        Assert.Contains(changes, c => c is { Workload: "old", Alert: "stalled", Change: "removed", After: null });
        Assert.Contains(changes, c => c is { Workload: "sync", Alert: "stalled", Change: "added", After: "no heartbeat for 1m, critical" });
        Assert.Empty(ManifestReview.Compare([after], [after], s_options));
    }

    [Fact]
    public async Task Validate_exits_with_1_on_errors_and_with_strict_on_warnings()
    {
        var good = Write("good.json", """{"version":1,"service":"billing","workloads":[{"name":"nightly","schedule":"0 2 * * *","severity":"critical"}]}""");
        var warns = Write("warns.json", """{"version":1,"service":"billing","workloads":[{"name":"idle","severity":"info"}]}""");
        var broken = Write("broken.json", "{ not json");

        Assert.Equal(0, await RunSignalCli.RunAsync(["validate", good], _output, _error));
        Assert.Contains("Manifests are valid.", _output.ToString());
        Assert.Equal(0, await RunSignalCli.RunAsync(["validate", warns], _output, _error));
        Assert.Equal(1, await RunSignalCli.RunAsync(["validate", warns, "--strict"], _output, _error));
        Assert.Equal(1, await RunSignalCli.RunAsync(["validate", good, broken], _output, _error));
        Assert.Contains("error: ", _output.ToString());
        Assert.Contains("broken.json: cannot be read", _output.ToString());
    }

    [Fact]
    public async Task Compare_writes_a_markdown_comment_with_the_changes_and_the_findings()
    {
        var before = Write("before.json", """{"version":1,"service":"billing","workloads":[{"name":"nightly","schedule":"0 2 * * *","severity":"warning"}]}""");
        var after = Write("after.json", """{"version":1,"service":"billing","workloads":[{"name":"nightly","schedule":"0 2 * * *","severity":"critical"},{"name":"idle","severity":"info"}]}""");

        Assert.Equal(0, await RunSignalCli.RunAsync(["compare", after, "--base", before, "--markdown"], _output, _error));

        var markdown = _output.ToString();
        Assert.StartsWith("### RunSignal alerts", markdown, StringComparison.Ordinal);
        Assert.Contains("| billing | `nightly` | missed | ~ changed | no success for 24.3h, warning | no success for 24.3h, critical |", markdown);
        Assert.Contains(":warning:", markdown);
        Assert.Contains("idle: no alerts", markdown);
    }
}
