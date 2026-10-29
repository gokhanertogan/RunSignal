using RunSignal.Alerting;
using RunSignal.Prometheus;

namespace RunSignal.Tests;

public class PrometheusTests
{
    private static string Fixture(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "prometheus", file)))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "tests", "prometheus", file);
    }

    // tests/prometheus/rules.yml is what `promtool test rules` checks; it must be exactly what the generator writes.
    [Fact]
    public void Generated_rules_match_the_file_promtool_tests()
    {
        var manifest = WorkloadManifest.FromJson(File.ReadAllText(Fixture("manifest.json")));

        var rules = PrometheusRules.Generate([manifest], new AlertOptions(), out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(File.ReadAllText(Fixture("rules.yml")).ReplaceLineEndings(), rules.ReplaceLineEndings());
    }

    [Fact]
    public void Workloads_without_rules_are_reported()
    {
        var manifest = new WorkloadManifest
        {
            Service = "billing",
            Workloads =
            [
                new WorkloadManifestEntry { Name = "bare" },
                new WorkloadManifestEntry { Name = "huge", MaxDurationSeconds = 100_000 },
            ],
        };

        var rules = PrometheusRules.Generate([manifest], new AlertOptions(), out var warnings);

        Assert.Contains(warnings, w => w.StartsWith("bare: no rules"));
        Assert.Contains(warnings, w => w.StartsWith("huge: no overrun rule"));
        Assert.DoesNotContain("alert:", rules);
    }

    [Fact]
    public void Label_values_are_escaped()
    {
        var manifest = new WorkloadManifest
        {
            Service = "it's \"quoted\"",
            Workloads = [new WorkloadManifestEntry { Name = "job", ExpectEverySeconds = 60 }],
        };

        var rules = PrometheusRules.Generate([manifest], new AlertOptions(), out _);

        Assert.Contains("job=\"it''s \\\"quoted\\\"\"", rules); // PromQL escapes the double quotes, YAML doubles the single quote
    }

    [Fact]
    public void Grafana_dashboard_selects_the_manifest_services_and_queries_the_rule_metrics()
    {
        var manifest = WorkloadManifest.FromJson(File.ReadAllText(Fixture("manifest.json")));

        var json = GrafanaDashboard.Generate([manifest, manifest]);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal($"RunSignal: {manifest.Service}", root.GetProperty("title").GetString());
        Assert.Equal(json, GrafanaDashboard.Generate([manifest])); // duplicates collapse, and the uid is stable
        var job = root.GetProperty("templating").GetProperty("list").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "job");
        Assert.Equal([manifest.Service], job.GetProperty("current").GetProperty("value").EnumerateArray().Select(v => v.GetString()!));

        var expressions = root.GetProperty("panels").EnumerateArray()
            .Select(p => p.GetProperty("targets")[0].GetProperty("expr").GetString()!).ToList();
        Assert.Equal(8, expressions.Count);
        foreach (var metric in new[] { "runsignal_workload_runs_total", "runsignal_workload_items_total", "runsignal_workload_duration_seconds_bucket",
                     "runsignal_workload_since_success_seconds", "runsignal_workload_since_heartbeat_seconds", "ALERTS" })
            Assert.Contains(expressions, e => e.Contains(metric, StringComparison.Ordinal));
    }

    [Fact]
    public void Grafana_dashboard_without_manifests_shows_every_service()
    {
        using var document = System.Text.Json.JsonDocument.Parse(GrafanaDashboard.Generate([]));

        Assert.Equal("runsignal", document.RootElement.GetProperty("uid").GetString());
        Assert.Equal("RunSignal", document.RootElement.GetProperty("title").GetString());
    }
}
