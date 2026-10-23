using System.Text.Json.Nodes;
using RunSignal.NewRelic;

namespace RunSignal.Tests;

public sealed class DashboardTests : IDisposable
{
    private readonly FakeNerdGraph _nerdGraph = new();
    private readonly HttpClient _http;
    private readonly NewRelicDashboards _dashboards;

    public DashboardTests()
    {
        _http = new HttpClient(_nerdGraph);
        _dashboards = new NewRelicDashboards(new NerdGraphClient(_http, "NRAK-test"), accountId: 42) { SearchableTimeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public void Widgets_query_the_service_and_do_not_overlap()
    {
        var widgets = NewRelicDashboards.Widgets("it's billing");

        Assert.All(widgets, w => Assert.Contains("service.name = 'it\\'s billing'", w.Nrql));
        var cells = widgets.SelectMany(w =>
            Enumerable.Range(w.Column, w.Width).SelectMany(c => Enumerable.Range(w.Row, w.Height).Select(r => (c, r)))).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
        Assert.All(widgets, w => Assert.InRange(w.Column + w.Width - 1, 1, 12));
    }

    [Fact]
    public async Task Provision_creates_once_then_changes_nothing()
    {
        Assert.Equal([new Change(ChangeKind.Create, "dashboard", "RunSignal: billing")], await _dashboards.ProvisionAsync("billing", dryRun: false));

        Assert.Equal([new Change(ChangeKind.Unchanged, "dashboard", "RunSignal: billing")], await _dashboards.ProvisionAsync("billing", dryRun: false));
        var dashboard = Assert.Single(_nerdGraph.Dashboards.Values);
        Assert.StartsWith(AlertPlanner.ManagedMarker, dashboard["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changed_widgets_are_updated()
    {
        await _dashboards.ProvisionAsync("billing", dryRun: false);
        _nerdGraph.Dashboards.Values.Single()["pages"]![0]!["widgets"]![0]!["title"] = "Edited by hand";

        Assert.Equal(ChangeKind.Update, Assert.Single(await _dashboards.ProvisionAsync("billing", dryRun: false)).Kind);
        Assert.Equal("Runs in the last hour", _nerdGraph.Dashboards.Values.Single()["pages"]![0]!["widgets"]![0]!["title"]!.GetValue<string>());
    }

    // New Relic's entity search lags behind writes: without waiting, a second run would create a duplicate.
    [Fact]
    public async Task A_new_dashboard_is_searchable_before_provision_returns()
    {
        _nerdGraph.SearchLag = 2;

        await _dashboards.ProvisionAsync("billing", dryRun: false);
        var second = await _dashboards.ProvisionAsync("billing", dryRun: false);

        Assert.Single(_nerdGraph.Dashboards);
        Assert.Equal(ChangeKind.Unchanged, Assert.Single(second).Kind);
    }

    [Fact]
    public async Task Duplicates_are_removed_but_dashboards_made_by_hand_are_kept()
    {
        await _dashboards.ProvisionAsync("billing", dryRun: false);
        var duplicate = _nerdGraph.AddDashboard((JsonObject)_nerdGraph.Dashboards.Values.Single().DeepClone());
        var handMade = _nerdGraph.AddDashboard(new JsonObject { ["name"] = "RunSignal: billing", ["description"] = "Mine.", ["pages"] = new JsonArray() });

        var changes = await _dashboards.ProvisionAsync("billing", dryRun: false);

        Assert.Contains(new Change(ChangeKind.Delete, "dashboard", "RunSignal: billing (duplicate)"), changes);
        Assert.DoesNotContain(duplicate, _nerdGraph.Dashboards.Keys);
        Assert.Contains(handMade, _nerdGraph.Dashboards.Keys);
        Assert.Equal(2, _nerdGraph.Dashboards.Count);
    }

    [Fact]
    public async Task Destroy_deletes_only_RunSignal_dashboards()
    {
        await _dashboards.ProvisionAsync("billing", dryRun: false);
        var handMade = _nerdGraph.AddDashboard(new JsonObject { ["name"] = "RunSignal: billing", ["description"] = "Mine.", ["pages"] = new JsonArray() });

        var dry = await _dashboards.DestroyAsync("billing", dryRun: true);
        Assert.Single(dry);
        Assert.Equal(2, _nerdGraph.Dashboards.Count);

        await _dashboards.DestroyAsync("billing", dryRun: false);
        Assert.Equal([handMade], _nerdGraph.Dashboards.Keys);
    }
}
