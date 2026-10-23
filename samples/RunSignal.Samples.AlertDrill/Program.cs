using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using RunSignal;

// An alert drill: each workload breaks one expectation on purpose, so you can check that every alert kind
// reaches you before a real job fails. Provision its alerts, run it, and watch the incidents open:
//
//   RUNSIGNAL_MANIFEST_PATH=drill.json dotnet run      (writes the manifest, then keeps running)
//   runsignal provision drill.json --account-id ... --region ...
//
// Expected incidents, as observed on New Relic: overrun and stalled within about 5 minutes; failure rate and
// item failure rate within about 30 minutes (their 15-minute window has to close); low output after its one-hour
// window closes. Remove them with `runsignal destroy`.
// Signals go to New Relic over OTLP with NEW_RELIC_LICENSE_KEY (and NEW_RELIC_REGION=EU for the EU data center).
var builder = Host.CreateApplicationBuilder(args);
var licenseKey = Environment.GetEnvironmentVariable("NEW_RELIC_LICENSE_KEY")
    ?? throw new InvalidOperationException("Set NEW_RELIC_LICENSE_KEY: the drill sends its signals to New Relic.");
var endpoint = new Uri(string.Equals(Environment.GetEnvironmentVariable("NEW_RELIC_REGION"), "EU", StringComparison.OrdinalIgnoreCase)
    ? "https://otlp.eu01.nr-data.net:4317"
    : "https://otlp.nr-data.net:4317");

builder.Services.AddRunSignal(o => o.ServiceName = "runsignal-alert-drill")
    // Every run takes 8 seconds against a 5-second limit.
    .AddWorkload("drill-overrun", o =>
    {
        o.Schedule = "* * * * *";
        o.MaxDuration = TimeSpan.FromSeconds(5);
        o.Severity = Severity.Critical;
    })
    // Every run processes 2 items against a minimum of 10.
    .AddWorkload("drill-low-output", o => o.MinProcessed = 10)
    // Six runs in ten fail, and so do six items in ten, against a 20% threshold.
    .AddWorkload("drill-failure-rate", o => o.FailureRateThreshold = 0.2)
    // Beats once, then never again, against a one-minute expectation; the process keeps reporting.
    .AddWorkload("drill-stalled", o => o.ExpectEvery = TimeSpan.FromMinutes(1));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("runsignal-alert-drill"))
    .WithMetrics(m => m
        .AddRunSignalInstrumentation()
        .AddOtlpExporter((exporter, reader) =>
        {
            exporter.Endpoint = endpoint;
            exporter.Protocol = OtlpExportProtocol.Grpc;
            exporter.Headers = $"api-key={licenseKey}";
            reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 30_000;
        }));

builder.Services.AddHostedService<Drill>();
builder.Build().Run();

internal sealed class Drill(
    [FromKeyedServices("drill-overrun")] Workload overrun,
    [FromKeyedServices("drill-low-output")] Workload lowOutput,
    [FromKeyedServices("drill-failure-rate")] Workload failureRate,
    [FromKeyedServices("drill-stalled")] Workload stalled,
    ILogger<Drill> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stop)
    {
        stalled.Beat();
        log.LogInformation("Alert drill running: overrun, low output, failure rate and stalled are being broken on purpose.");
        return Task.WhenAll(
            Every(TimeSpan.FromMinutes(1), () => overrun.RunAsync(ct => Task.Delay(TimeSpan.FromSeconds(8), ct), stop), stop),
            Every(TimeSpan.FromSeconds(30), () => lowOutput.RunAsync(_ =>
            {
                lowOutput.Processed(2);
                return Task.CompletedTask;
            }, stop), stop),
            Every(TimeSpan.FromSeconds(10), async () =>
            {
                var failing = Random.Shared.NextDouble() < 0.6;
                if (failing) failureRate.ItemFailed(); else failureRate.Processed();
                try
                {
                    await failureRate.RunAsync(_ => failing ? throw new TimeoutException("drill") : Task.CompletedTask, stop);
                }
                catch (TimeoutException)
                {
                    // Recorded as a failed run, on purpose.
                }
            }, stop));
    }

    private static async Task Every(TimeSpan interval, Func<Task> action, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            await action();
        }
        while (await timer.WaitForNextTickAsync(stop));
    }
}
