using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RunSignal;

// Two workloads that HTTP monitoring never sees: a loop that syncs stock, and a job that imports invoices.
// Metrics are printed to the console every 10 seconds; point an OTLP exporter at New Relic instead in production.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRunSignal()
    .AddWorkload("stock-sync", o =>
    {
        o.ExpectEvery = TimeSpan.FromMinutes(5);
        o.FailureRateThreshold = 0.2;
    })
    .AddWorkload("invoice-import", o =>
    {
        o.Schedule = "*/1 * * * *";
        o.MaxDuration = TimeSpan.FromSeconds(30);
        o.MinProcessed = 1;
        o.Severity = Severity.Critical;
    });

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("runsignal-sample"))
    .WithMetrics(m => m
        .AddRunSignalInstrumentation()
        .AddConsoleExporter((_, reader) => reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000))
    .WithTracing(t => t
        .AddRunSignalInstrumentation()
        .AddConsoleExporter());

builder.Services.AddHostedService<StockSyncWorker>();
builder.Services.AddHostedService<InvoiceImportWorker>();

builder.Build().Run();

/// <summary>A long-running loop: a heartbeat on every pass reveals a loop that is alive but stuck.</summary>
internal sealed class StockSyncWorker([FromKeyedServices("stock-sync")] Workload workload) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var updated = Random.Shared.Next(0, 50);
            workload.Processed(updated);
            if (Random.Shared.NextDouble() < 0.1)
                workload.ItemFailed();

            workload.Beat();
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

/// <summary>A scheduled job: each pass is one run that succeeds, fails or is cancelled.</summary>
internal sealed class InvoiceImportWorker([FromKeyedServices("invoice-import")] Workload workload) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await workload.RunAsync(ImportAsync, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already recorded as a failed run; keep the loop going.
            }
        }
    }

    private async Task ImportAsync(CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(100, 800), ct);
        if (Random.Shared.NextDouble() < 0.2)
            throw new TimeoutException("Supplier API did not answer.");
        workload.Processed(Random.Shared.Next(1, 20));
    }
}
