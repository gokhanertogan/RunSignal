using RunSignal.Cli;

namespace RunSignal.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _manifest = Path.Combine(Path.GetTempPath(), $"runsignal-cli-{Guid.NewGuid():N}.json");
    private readonly FakeNerdGraph _nerdGraph = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public CliTests()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("nightly-invoice", o =>
        {
            o.Schedule = "0 2 * * *";
            o.Severity = Severity.Critical;
        });
        File.WriteAllText(_manifest, registry.CreateManifest("billing").ToJson());
    }

    public void Dispose() => File.Delete(_manifest);

    private Task<int> Run(params string[] args) => RunSignalCli.RunAsync(args, _output, _error, _nerdGraph);

    [Fact]
    public async Task Plan_prints_conditions_without_contacting_new_relic()
    {
        Assert.Equal(0, await Run("plan", _manifest));

        var text = _output.ToString();
        Assert.Contains("policy RunSignal: billing (critical)", text);
        Assert.Contains("nightly-invoice: missed", text);
        Assert.Empty(_nerdGraph.Operations);
    }

    [Fact]
    public async Task Provision_then_diff_reports_no_changes()
    {
        Assert.Equal(0, await Run("provision", _manifest, "--account-id", "42", "--api-key", "NRAK-x", "--region", "EU", "--channel", "critical=teams-1"));
        Assert.Contains("+ condition nightly-invoice: missed", _output.ToString());
        Assert.Contains("+ workflow RunSignal: billing (critical)", _output.ToString());
        Assert.Single(_nerdGraph.Workflows);

        _output.GetStringBuilder().Clear();
        Assert.Equal(0, await Run("diff", _manifest, "--account-id", "42", "--api-key", "NRAK-x", "--region", "EU", "--channel", "critical=teams-1"));
        Assert.EndsWith("No changes." + Environment.NewLine, _output.ToString());
    }

    [Fact]
    public async Task Destroy_removes_the_provisioned_alerts()
    {
        await Run("provision", _manifest, "--account-id", "42", "--api-key", "NRAK-x");
        _output.GetStringBuilder().Clear();

        Assert.Equal(0, await Run("destroy", "--service", "billing", "--account-id", "42", "--api-key", "NRAK-x"));

        Assert.Contains("- policy RunSignal: billing (critical)", _output.ToString());
        Assert.Empty(_nerdGraph.Policies);
        Assert.Empty(_nerdGraph.Conditions);
    }

    [Fact]
    public async Task Errors_are_one_line_and_exit_with_1()
    {
        Assert.Equal(1, await Run("plan", "/does/not/exist.json"));
        Assert.StartsWith("error: ", _error.ToString());
        Assert.DoesNotContain("   at ", _error.ToString());
    }

    [Fact]
    public void Channels_are_grouped_by_severity()
    {
        var routes = RunSignalCli.ParseChannels(["critical=a", "Critical=b", "warning=c"]);
        Assert.Equal(["a", "b"], routes[Severity.Critical]);
        Assert.Equal(["c"], routes[Severity.Warning]);
        Assert.Throws<ArgumentException>(() => RunSignalCli.ParseChannels(["page=a"]));
    }
}
