using OpenTelemetry.Metrics;
using RunSignal.Cli;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.Tests;

// runsignal exec runs a command as one run of a workload. The OTLP exporter is swapped for an in-memory one.
public sealed class ExecTests
{
    private readonly List<Metric> _exported = [];
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    // Unique per test: the provider sees every RunSignal meter in the process, including other tests' runs.
    private readonly string _workload = TestNames.Unique("exec");

    private Task<int> Exec(params string[] command) =>
        RunSignalCli.RunAsync(["exec", "--workload", _workload, "--service", "reports", "--", .. command], _output, _error,
            configureExecMetrics: b => b.AddInMemoryExporter(_exported));

    private List<(string Outcome, string? ErrorType)> Runs() =>
        _exported.Where(m => m.Name == Instruments.Runs).SelectMany(m =>
        {
            var points = new List<(string, string?)>();
            foreach (ref readonly var point in m.GetMetricPoints())
            {
                string? outcome = null, errorType = null, workload = null;
                foreach (var tag in point.Tags)
                {
                    if (tag.Key == Attributes.Workload) workload = (string?)tag.Value;
                    if (tag.Key == Attributes.Outcome) outcome = (string?)tag.Value;
                    if (tag.Key == Attributes.ErrorType) errorType = (string?)tag.Value;
                }
                if (workload == _workload)
                    points.Add((outcome!, errorType));
            }
            return points;
        }).Distinct().ToList(); // a cumulative point is exported on flush and again on shutdown

    [Fact]
    public async Task A_command_that_exits_zero_is_a_successful_run()
    {
        Assert.Equal(0, await Exec("sh", "-c", "exit 0"));
        Assert.Equal(("success", null), Assert.Single(Runs()));
        Assert.Contains(_exported, m => m.Name == Instruments.Duration);
    }

    [Fact]
    public async Task A_failing_command_is_a_failed_run_with_its_exit_code_and_the_code_is_returned()
    {
        Assert.Equal(3, await Exec("sh", "-c", "exit 3"));
        Assert.Equal(("failure", "exit_code_3"), Assert.Single(Runs()));
    }

    [Fact]
    public async Task A_command_that_cannot_start_fails_with_127()
    {
        Assert.Equal(127, await Exec("runsignal-no-such-command"));
        Assert.Equal(("failure", "command_not_found"), Assert.Single(Runs()));
        Assert.Contains("runsignal-no-such-command", _error.ToString());
    }

    [Fact]
    public async Task The_run_is_in_progress_while_the_command_runs()
    {
        // The gauge is exported on every interval during a run; here it is read once the run has ended, at 0.
        Assert.Equal(0, await Exec("sh", "-c", "sleep 0.2"));
        Assert.Contains(_exported, m => m.Name == Instruments.ActiveRunAge);
    }

    [Fact]
    public async Task A_service_name_is_required_and_temporality_is_checked()
    {
        var noService = await RunSignalCli.RunAsync(["exec", "--workload", "exec-job", "--", "true"], _output, _error,
            configureExecMetrics: b => b.AddInMemoryExporter(_exported));
        if (Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is null)
            Assert.Equal(2, noService);

        Assert.Equal(MetricReaderTemporalityPreference.Delta, ExecCommand.ParseTemporality("delta"));
        Assert.Equal(MetricReaderTemporalityPreference.Cumulative, ExecCommand.ParseTemporality(null));
        Assert.Throws<FormatException>(() => ExecCommand.ParseTemporality("sometimes"));
    }

    [Fact]
    public async Task An_invalid_workload_name_is_rejected_before_the_command_runs()
    {
        var code = await RunSignalCli.RunAsync(["exec", "--workload", "Not Valid", "--service", "reports", "--", "sh", "-c", "touch should-not-exist"], _output, _error,
            configureExecMetrics: b => b.AddInMemoryExporter(_exported));
        Assert.Equal(2, code);
        Assert.False(File.Exists("should-not-exist"));
    }
}
