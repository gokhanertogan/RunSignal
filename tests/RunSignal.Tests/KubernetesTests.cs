using System.Text.Json;
using RunSignal.Cli;
using RunSignal.NewRelic;

namespace RunSignal.Tests;

// runsignal discover kubernetes, from tests/kubernetes/cronjobs.json (kubectl get cronjobs -o json).
public sealed class KubernetesTests : IDisposable
{
    private readonly string _manifest = Path.Combine(Path.GetTempPath(), $"runsignal-k8s-{Guid.NewGuid():N}.json");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose() => File.Delete(_manifest);

    private static string Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "kubernetes")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "tests", "kubernetes", "cronjobs.json");
    }

    private static WorkloadManifest Discover(string json, out IReadOnlyList<string> warnings)
    {
        using var document = JsonDocument.Parse(json);
        return KubernetesCronJobs.ToManifest(document.RootElement, "billing", out warnings);
    }

    [Fact]
    public async Task CronJobs_become_per_run_workloads_with_their_schedules_zones_and_deadlines()
    {
        Assert.Equal(0, await RunSignalCli.RunAsync(["discover", "kubernetes", "--service", "billing", "--input", Fixture(), "-o", _manifest], _output, _error));

        var manifest = WorkloadManifest.FromJson(File.ReadAllText(_manifest));
        Assert.Equal("billing", manifest.Service);
        Assert.Equal(["bank-statement-import", "cache-warm", "invoice-export"], manifest.Workloads.Select(w => w.Name));
        Assert.All(manifest.Workloads, w => Assert.Equal(WorkloadReporting.PerRun, w.Reporting));

        var invoice = manifest.Workloads.Single(w => w.Name == "invoice-export"); // named after InvoiceExport
        Assert.Equal("0 2 * * *", invoice.Schedule);
        Assert.Equal("Europe/Istanbul", invoice.TimeZone); // spec.timeZone
        Assert.Equal(1800, invoice.MaxDurationSeconds); // activeDeadlineSeconds
        Assert.Equal(100, invoice.MinProcessed);
        Assert.Equal(Severity.Critical, invoice.Severity);

        Assert.Equal("0 * * * *", manifest.Workloads.Single(w => w.Name == "cache-warm").Schedule); // @hourly

        var statements = manifest.Workloads.Single(w => w.Name == "bank-statement-import"); // runsignal.io/workload
        Assert.Equal("*/15 6-20 * * 1-5", statements.Schedule);
        Assert.Equal("Europe/Berlin", statements.TimeZone); // CRON_TZ= prefix
        Assert.Equal(2700, statements.MaxDurationSeconds); // the annotation wins over activeDeadlineSeconds
        Assert.Equal(0.2, statements.FailureRateThreshold);

        var warnings = _error.ToString();
        Assert.Contains("billing/old-report: skipped, because it is suspended", warnings);
        Assert.Contains("billing/poller: skipped, because its schedule '@every 5m'", warnings);
        Assert.DoesNotContain("debug-dump", warnings + File.ReadAllText(_manifest)); // runsignal.io/ignore
    }

    [Fact]
    public async Task A_discovered_manifest_plans_per_run_alerts()
    {
        await RunSignalCli.RunAsync(["discover", "kubernetes", "--service", "billing", "--input", Fixture(), "-o", _manifest], _output, _error);
        var plan = AlertPlanner.Plan(WorkloadManifest.FromJson(File.ReadAllText(_manifest)),
            new AlertPlanOptions { Now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero) });

        var missed = plan.Policies.SelectMany(p => p.Conditions).Where(c => c.Kind == AlertKind.Missed).ToList();
        Assert.Equal(["cache-warm", "invoice-export"], missed.Select(c => c.Workload).Order());
        Assert.All(missed, c => Assert.Contains("runsignal.outcome = 'success'", c.Nrql));
        // Friday 20:45 to Monday 06:00 plus 45 minutes is beyond New Relic's 48-hour loss of signal.
        Assert.Contains(plan.Warnings, w => w.StartsWith("bank-statement-import: no missed-run alert, because New Relic cannot wait", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_cronjobs_that_become_the_same_workload_are_an_error()
    {
        const string json = """
            { "items": [
              { "metadata": { "name": "sync", "namespace": "a" }, "spec": { "schedule": "0 * * * *" } },
              { "metadata": { "name": "sync", "namespace": "b" }, "spec": { "schedule": "0 * * * *" } } ] }
            """;
        var ex = Assert.Throws<FormatException>(() => Discover(json, out _));
        Assert.Contains("a/sync and b/sync both become workload 'sync'", ex.Message);
    }

    [Theory]
    [InlineData("runsignal.io/severity", "urgent")]
    [InlineData("runsignal.io/max-duration", "soon")]
    [InlineData("runsignal.io/min-processed", "0")]
    [InlineData("runsignal.io/failure-rate", "20%")]
    [InlineData("runsignal.io/workload", "Not Valid")]
    public void Invalid_annotations_name_the_cronjob(string annotation, string value)
    {
        var json = $$"""
            { "kind": "CronJob", "metadata": { "name": "sync", "namespace": "a", "annotations": { "{{annotation}}": "{{value}}" } },
              "spec": { "schedule": "0 * * * *" } }
            """;
        var ex = Assert.Throws<FormatException>(() => Discover(json, out _));
        Assert.StartsWith("a/sync: ", ex.Message);
    }

    [Theory]
    [InlineData("@daily", "0 0 * * *", null)]
    [InlineData("TZ=UTC @weekly", "0 0 * * 0", "UTC")]
    [InlineData("  0   3 * * *  ", "0 3 * * *", null)]
    [InlineData("@every 1h", null, null)]
    public void Schedules_are_normalized_to_plain_cron(string schedule, string? cron, string? zone)
    {
        Assert.Equal((cron, zone), KubernetesCronJobs.NormalizeSchedule(schedule));
    }

    [Fact]
    public void Input_that_is_not_cronjobs_is_rejected()
    {
        Assert.Throws<FormatException>(() => Discover("""{ "kind": "Deployment" }""", out _));
    }
}
