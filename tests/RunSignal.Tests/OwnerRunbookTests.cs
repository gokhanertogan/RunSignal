using RunSignal.Alerting;
using RunSignal.Cli;
using RunSignal.NewRelic;
using RunSignal.Prometheus;

namespace RunSignal.Tests;

// owner and runbookUrl: who answers a workload's alerts and what to do, carried into every backend.
public sealed class OwnerRunbookTests
{
    private static readonly AlertPlanOptions s_options = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    private const string Runbook = "https://wiki.example.com/runbooks/nightly-invoice";

    private static WorkloadManifest Manifest(WorkloadManifestEntry entry) => new() { Service = "billing", Workloads = [entry] };

    [Fact]
    public void The_manifest_writes_them_after_severity_and_omits_them_when_unset()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("nightly-invoice", o =>
        {
            o.Schedule = "0 2 * * *";
            o.Severity = Severity.Critical;
            o.Owner = "billing-team";
            o.RunbookUrl = Runbook;
        });
        registry.Get("plain", o => o.Schedule = "0 3 * * *");

        var json = registry.CreateManifest("billing").ToJson();

        // Every SDK writes the same bytes: owner and runbookUrl follow severity.
        Assert.Contains($"\"severity\": \"critical\",\n      \"owner\": \"billing-team\",\n      \"runbookUrl\": \"{Runbook}\"", json.ReplaceLineEndings("\n"));
        var plain = WorkloadManifest.FromJson(json).Workloads.Single(w => w.Name == "plain");
        Assert.Null(plain.Owner);
        Assert.Equal(2, json.Split("owner").Length); // only nightly-invoice has one
    }

    [Fact]
    public void Strings_are_written_with_only_the_escapes_json_requires_as_every_sdk_writes_them()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("nightly-invoice", o =>
        {
            o.Schedule = "0 2 * * *";
            o.Severity = Severity.Critical;
            o.Owner = "Gökhan & Billing <ops>";
            o.RunbookUrl = "https://wiki.example.com/runbooks?job=nightly&env=prod";
        });

        // The same bytes the Python and Node.js SDKs write for this workload (no \u00F6 or \u0026).
        Assert.Equal("""
            {
              "version": 1,
              "service": "billing",
              "workloads": [
                {
                  "name": "nightly-invoice",
                  "schedule": "0 2 * * *",
                  "severity": "critical",
                  "owner": "Gökhan & Billing <ops>",
                  "runbookUrl": "https://wiki.example.com/runbooks?job=nightly&env=prod"
                }
              ]
            }
            """, registry.CreateManifest("billing").ToJson().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("ftp://example.com/runbook")]
    [InlineData("/runbooks/nightly")]
    [InlineData("not a url")]
    public void Runbook_urls_must_be_absolute_http_or_https(string url) =>
        Assert.Throws<ArgumentException>(() => new WorkloadOptions().RunbookUrl = url);

    [Fact]
    public void Owners_must_be_1_to_128_characters()
    {
        Assert.Throws<ArgumentException>(() => new WorkloadOptions().Owner = " ");
        Assert.Throws<ArgumentException>(() => new WorkloadOptions().Owner = new string('a', 129));
        Assert.Equal("billing-team", new WorkloadOptions { Owner = "billing-team" }.Owner);
    }

    [Fact]
    public void The_attribute_sets_them()
    {
        var options = new WorkloadOptions();
        new WorkloadAttribute("nightly-invoice") { Owner = "billing-team", RunbookUrl = Runbook }.ApplyTo(options);
        Assert.Equal(("billing-team", Runbook), (options.Owner, options.RunbookUrl));
    }

    [Fact]
    public void Every_new_relic_condition_names_the_owner_and_the_runbook()
    {
        var plan = AlertPlanner.Plan(Manifest(new WorkloadManifestEntry
        {
            Name = "nightly-invoice", Schedule = "0 2 * * *", MaxDurationSeconds = 1800, Owner = "billing-team", RunbookUrl = Runbook,
        }), s_options);

        var conditions = plan.Policies.SelectMany(p => p.Conditions).ToList();
        Assert.NotEmpty(conditions);
        Assert.All(conditions, c => Assert.EndsWith($" Owner: billing-team. Runbook: {Runbook}", c.Description, StringComparison.Ordinal));
        Assert.All(conditions, c => Assert.StartsWith(AlertPlanner.ManagedMarker, c.Description, StringComparison.Ordinal));
    }

    [Fact]
    public void Prometheus_rules_carry_the_owner_label_and_the_runbook_annotation()
    {
        var rules = PrometheusRules.Generate([Manifest(new WorkloadManifestEntry
        {
            Name = "nightly-invoice", Schedule = "0 2 * * *", Owner = "billing-team", RunbookUrl = Runbook,
        })], new AlertOptions(), out _);

        Assert.Contains("          runsignal_workload: 'nightly-invoice'\n          owner: 'billing-team'\n        annotations:", rules.ReplaceLineEndings("\n"));
        Assert.Contains($"          runbook_url: '{Runbook}'", rules);
        Assert.Equal(rules.Split("- alert:").Length - 1, rules.Split("owner: 'billing-team'").Length - 1); // on every rule
    }

    [Fact]
    public void Kubernetes_annotations_set_them_and_bad_values_name_the_cronjob()
    {
        static WorkloadManifest Discover(string annotations)
        {
            using var document = System.Text.Json.JsonDocument.Parse($$"""
                { "kind": "CronJob", "metadata": { "name": "sync", "namespace": "a", "annotations": { {{annotations}} } },
                  "spec": { "schedule": "0 * * * *" } }
                """);
            return KubernetesCronJobs.ToManifest(document.RootElement, "billing", out _);
        }

        var workload = Discover($"\"runsignal.io/owner\": \"billing-team\", \"runsignal.io/runbook-url\": \"{Runbook}\"").Workloads.Single();
        Assert.Equal(("billing-team", Runbook), (workload.Owner, workload.RunbookUrl));
        Assert.StartsWith("a/sync: ", Assert.Throws<FormatException>(() => Discover("\"runsignal.io/runbook-url\": \"wiki/sync\"")).Message);
    }

    [Fact]
    public void Validate_rejects_a_bad_runbook_url_and_compare_shows_an_owner_change()
    {
        var bad = Manifest(new WorkloadManifestEntry { Name = "sync", ExpectEverySeconds = 60, RunbookUrl = "wiki/sync" });
        Assert.Contains(ManifestReview.Validate([("a.json", bad)], s_options),
            f => f is { IsError: true, Message: "sync: runbookUrl 'wiki/sync' is not an absolute http or https URL." });

        var before = Manifest(new WorkloadManifestEntry { Name = "sync", ExpectEverySeconds = 60, Owner = "ops" });
        var after = Manifest(new WorkloadManifestEntry { Name = "sync", ExpectEverySeconds = 60, Owner = "billing-team" });
        var change = Assert.Single(ManifestReview.Compare([before], [after], s_options));
        Assert.Equal(("no heartbeat for 1m, warning, owner ops", "no heartbeat for 1m, warning, owner billing-team"), (change.Before, change.After));
    }
}
