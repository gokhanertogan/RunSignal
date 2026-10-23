using RunSignal.NewRelic;

namespace RunSignal.Tests;

public class AlertPlannerTests
{
    private static readonly AlertPlanOptions s_options = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };

    private static WorkloadManifest Manifest(params WorkloadManifestEntry[] workloads) =>
        new() { Service = "billing", Workloads = workloads };

    private static AlertCondition Single(AlertPlan plan, AlertKind kind) =>
        Assert.Single(plan.Policies.SelectMany(p => p.Conditions), c => c.Kind == kind);

    [Fact]
    public void Scheduled_workload_gets_missed_and_failed_alerts()
    {
        var plan = AlertPlanner.Plan(Manifest(new WorkloadManifestEntry { Name = "nightly-invoice", Schedule = "0 2 * * *", Severity = Severity.Critical }), s_options);

        var missed = Single(plan, AlertKind.Missed);
        Assert.Equal("nightly-invoice: missed", missed.Name);
        Assert.Equal("SELECT max(runsignal.workload.since_success) FROM Metric WHERE service.name = 'billing' AND runsignal.workload = 'nightly-invoice'", missed.Nrql);
        Assert.Equal(24 * 3600 + 15 * 60, missed.Threshold); // a day between runs plus the default grace
        Assert.True(missed.Above);
        Assert.Equal(900, missed.LossOfSignalSeconds);
        Assert.StartsWith(AlertPlanner.ManagedMarker, missed.Description);

        var failed = Single(plan, AlertKind.Failed);
        Assert.Equal(0, failed.Threshold);
        Assert.Equal(AggregationMethod.EventTimer, failed.AggregationMethod);

        var critical = Assert.Single(plan.Policies, p => p.Severity == Severity.Critical);
        Assert.Equal("RunSignal: billing (critical)", critical.Name);
        Assert.Equal(2, critical.Conditions.Count);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Max_duration_is_the_missed_grace_and_adds_an_overrun_alert()
    {
        var plan = AlertPlanner.Plan(Manifest(new WorkloadManifestEntry { Name = "report", Schedule = "0 9 * * 1-5", MaxDurationSeconds = 1800 }), s_options);

        Assert.Equal(72 * 3600 + 1800, Single(plan, AlertKind.Missed).Threshold); // Friday to Monday plus 30 minutes
        var overrun = Single(plan, AlertKind.Overrun);
        Assert.Equal(1800, overrun.Threshold);
        Assert.Contains("max(runsignal.workload.duration)", overrun.Nrql);
    }

    [Fact]
    public void Heartbeat_output_and_failure_rate_expectations_get_their_alerts()
    {
        var plan = AlertPlanner.Plan(Manifest(new WorkloadManifestEntry
        {
            Name = "stock-sync",
            ExpectEverySeconds = 300,
            MinProcessed = 10,
            FailureRateThreshold = 0.2,
        }), s_options);

        var stalled = Single(plan, AlertKind.Stalled);
        Assert.Equal(300, stalled.Threshold);
        Assert.Contains("since_heartbeat", stalled.Nrql);
        Assert.NotNull(stalled.LossOfSignalSeconds);

        var lowOutput = Single(plan, AlertKind.LowOutput);
        Assert.False(lowOutput.Above);
        Assert.Equal(10, lowOutput.Threshold);

        Assert.Equal(0.2, Single(plan, AlertKind.FailureRate).Threshold);
        Assert.Contains("More than 20% of the runs", Single(plan, AlertKind.FailureRate).Description);
        Assert.Equal(0.2, Single(plan, AlertKind.ItemFailureRate).Threshold);
        Assert.DoesNotContain(plan.Policies.SelectMany(p => p.Conditions), c => c.Kind == AlertKind.Failed);
        Assert.Equal("RunSignal: billing (warning)", Assert.Single(plan.Policies, p => p.Conditions.Count > 0).Name);
    }

    [Fact]
    public void Workloads_without_expectations_or_with_bad_schedules_are_reported()
    {
        var plan = AlertPlanner.Plan(Manifest(
            new WorkloadManifestEntry { Name = "bare" },
            new WorkloadManifestEntry { Name = "broken", Schedule = "every night" }), s_options);

        Assert.Empty(plan.Policies.SelectMany(p => p.Conditions));
        Assert.Contains(plan.Warnings, w => w.StartsWith("bare: no alerts"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("broken: no missed-run alert"));
        Assert.Equal(3, plan.Policies.Count); // empty policies are kept so removed conditions get deleted
    }

    [Fact]
    public void Service_names_are_escaped_in_nrql()
    {
        var plan = AlertPlanner.Plan(new WorkloadManifest
        {
            Service = "it's",
            Workloads = [new WorkloadManifestEntry { Name = "job", ExpectEverySeconds = 60 }],
        }, s_options);

        Assert.Contains("service.name = 'it\\'s'", Single(plan, AlertKind.Stalled).Nrql);
    }
}

public sealed class NewRelicProvisionerTests : IDisposable
{
    private static readonly AlertPlanOptions s_options = new() { Now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
    private static readonly IReadOnlyDictionary<Severity, IReadOnlyList<string>> s_noChannels = new Dictionary<Severity, IReadOnlyList<string>>();

    private readonly FakeNerdGraph _nerdGraph = new();
    private readonly HttpClient _http;
    private readonly NewRelicProvisioner _provisioner;

    public NewRelicProvisionerTests()
    {
        _http = new HttpClient(_nerdGraph);
        _provisioner = new NewRelicProvisioner(new NerdGraphClient(_http, "NRAK-test", NewRelicRegion.EU), accountId: 42);
    }

    public void Dispose() => _http.Dispose();

    private static AlertPlan Plan(params WorkloadManifestEntry[] workloads) =>
        AlertPlanner.Plan(new WorkloadManifest { Service = "billing", Workloads = workloads }, s_options);

    private static readonly WorkloadManifestEntry s_invoice = new() { Name = "nightly-invoice", Schedule = "0 2 * * *", Severity = Severity.Critical };

    [Fact]
    public async Task First_run_creates_policy_and_conditions_and_second_run_changes_nothing()
    {
        var plan = Plan(s_invoice);

        var first = await _provisioner.ProvisionAsync(plan, s_noChannels, dryRun: false);
        Assert.Equal(
            ["Create policy RunSignal: billing (critical)", "Create condition nightly-invoice: missed", "Create condition nightly-invoice: failed"],
            first.Select(c => $"{c.Kind} {c.Resource} {c.Name}"));
        Assert.Equal(2, _nerdGraph.Conditions.Count);
        Assert.Equal("NRAK-test", _nerdGraph.LastApiKey);
        Assert.Equal("api.eu.newrelic.com", _nerdGraph.LastUri!.Host);

        _nerdGraph.Operations.Clear();
        var second = await _provisioner.ProvisionAsync(plan, s_noChannels, dryRun: false);

        Assert.All(second, c => Assert.Equal(ChangeKind.Unchanged, c.Kind));
        Assert.DoesNotContain(_nerdGraph.Operations, o => o.StartsWith("create") || o.StartsWith("update") || o.StartsWith("delete"));
    }

    [Fact]
    public async Task Changed_expectations_update_and_removed_ones_delete_only_managed_conditions()
    {
        await _provisioner.ProvisionAsync(Plan(s_invoice), s_noChannels, dryRun: false);
        var policyId = _nerdGraph.Policies.Single().Key;
        _nerdGraph.AddUnmanagedCondition(policyId, "hand-made");

        // Now with a failure-rate threshold: "failed" is replaced by two rate conditions, "missed" is unchanged.
        var changes = await _provisioner.ProvisionAsync(Plan(s_invoice with { FailureRateThreshold = 0.5, MaxDurationSeconds = 600 }), s_noChannels, dryRun: false);

        Assert.Contains(new Change(ChangeKind.Update, "condition", "nightly-invoice: missed"), changes); // grace changed
        Assert.Contains(new Change(ChangeKind.Create, "condition", "nightly-invoice: failure rate"), changes);
        Assert.Contains(new Change(ChangeKind.Delete, "condition", "nightly-invoice: failed"), changes);
        Assert.DoesNotContain(changes, c => c.Name == "hand-made");
        Assert.Contains(_nerdGraph.Conditions.Values, c => c["name"]!.GetValue<string>() == "hand-made");
    }

    [Fact]
    public async Task Dry_run_reports_changes_without_making_them()
    {
        var changes = await _provisioner.ProvisionAsync(Plan(s_invoice), s_noChannels, dryRun: true);

        Assert.Contains(new Change(ChangeKind.Create, "policy", "RunSignal: billing (critical)"), changes);
        Assert.Contains(new Change(ChangeKind.Create, "condition", "nightly-invoice: missed"), changes);
        Assert.Empty(_nerdGraph.Policies);
        Assert.Empty(_nerdGraph.Conditions);
    }

    [Fact]
    public async Task Workflow_routes_the_policy_to_channels_and_follows_channel_changes()
    {
        var channels = new Dictionary<Severity, IReadOnlyList<string>> { [Severity.Critical] = ["teams-channel"] };
        await _provisioner.ProvisionAsync(Plan(s_invoice), channels, dryRun: false);

        var workflow = Assert.Single(_nerdGraph.Workflows.Values);
        Assert.Equal("RunSignal: billing (critical)", workflow["name"]!.GetValue<string>());
        var predicate = workflow["issuesFilter"]!["predicates"]![0]!;
        Assert.Equal("labels.policyIds", predicate["attribute"]!.GetValue<string>());
        Assert.Equal(_nerdGraph.Policies.Single().Key, predicate["values"]![0]!.GetValue<string>());

        var unchanged = await _provisioner.ProvisionAsync(Plan(s_invoice), channels, dryRun: false);
        Assert.Contains(new Change(ChangeKind.Unchanged, "workflow", "RunSignal: billing (critical)"), unchanged);

        channels[Severity.Critical] = ["teams-channel", "slack-channel"];
        var updated = await _provisioner.ProvisionAsync(Plan(s_invoice), channels, dryRun: false);
        Assert.Contains(new Change(ChangeKind.Update, "workflow", "RunSignal: billing (critical)"), updated);
        Assert.Equal(2, workflow["destinationConfigurations"]!.AsArray().Count);
    }

    [Fact]
    public async Task Destroy_removes_what_RunSignal_created_and_keeps_hand_made_conditions()
    {
        var channels = new Dictionary<Severity, IReadOnlyList<string>> { [Severity.Critical] = ["teams-channel"] };
        await _provisioner.ProvisionAsync(Plan(s_invoice, new WorkloadManifestEntry { Name = "stock-sync", ExpectEverySeconds = 300 }), channels, dryRun: false);
        var warningPolicy = _nerdGraph.Policies.Single(p => p.Value.EndsWith("(warning)")).Key;
        _nerdGraph.AddUnmanagedCondition(warningPolicy, "hand-made");

        var dry = await _provisioner.DestroyAsync("billing", dryRun: true);
        Assert.Contains(new Change(ChangeKind.Delete, "policy", "RunSignal: billing (critical)"), dry);
        Assert.Equal(2, _nerdGraph.Policies.Count); // nothing changed yet

        await _provisioner.DestroyAsync("billing", dryRun: false);

        Assert.Equal(["RunSignal: billing (warning)"], _nerdGraph.Policies.Values);
        Assert.Equal(["hand-made"], _nerdGraph.Conditions.Values.Select(c => c["name"]!.GetValue<string>()));
        Assert.Empty(_nerdGraph.Workflows);
        Assert.Empty(await _provisioner.DestroyAsync("someone-else", dryRun: false));
    }
}
