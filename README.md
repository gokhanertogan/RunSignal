# RunSignal

**Cross-language workload observability and alert provisioning.**

Your APM alerts on HTTP 500s and slow endpoints. But the failures that hurt most often happen where no HTTP
status code exists:

- The 02:00 invoice job never started, because the server restarted or the scheduler got stuck.
- A queue consumer is up but has not processed a message for 20 minutes.
- The bank statement file did not arrive, so the import "succeeded" with 0 rows.
- A `BackgroundService` loop is deadlocked, and its exception was swallowed in a `catch`.

RunSignal records **runs, items and heartbeats** of background work as OpenTelemetry metrics and spans, with the
same names in every language. From them it provisions the alerts you need, sent to Teams or Slack:
**missed**, **overrun**, **stalled**, **low-output** and **failure-rate**.

> Status: early development. The .NET core (`RunSignal.Core`, `RunSignal.DotNet`, `RunSignal.OpenTelemetry`)
> works; scheduler integrations, New Relic provisioning, the CLI and other languages are next.
> See [docs/ROADMAP.md](docs/ROADMAP.md).

## Why RunSignal

- **Lives in the stack you already have.** Signals are plain OpenTelemetry, so they go to New Relic, Grafana,
  Datadog or any OTLP backend. There is no new SaaS to sign up for.
- **Alerts as code.** Workload expectations (schedule, max duration, heartbeat interval, minimum output) live
  next to the code, and RunSignal provisions the matching alerts.
- **Knows your scheduler.** Hangfire and Quartz integrations (planned) monitor jobs without hand-written ping code.
- **One model, many languages.** .NET, Java, Go, Node.js and Rust emit the same signals defined in
  [spec/README.md](spec/README.md), so one set of alert rules covers all of them.
- **Built for hot paths.** Recording a run, an item or a heartbeat does not allocate.

## Where it fits

| Language | Typical tools | Scenarios |
|---|---|---|
| .NET | Hangfire, Quartz.NET, `BackgroundService`, MassTransit, Azure Functions timers | E-invoicing, nightly billing and reports, ERP and marketplace sync, Service Bus / RabbitMQ consumers |
| Java | Spring `@Scheduled`, Spring Batch, Quartz, Kafka | End-of-day banking batches, large ETL, Kafka consumers, reconciliation |
| Go | Kubernetes CronJobs, `robfig/cron`, goroutine workers, NATS / Kafka | Platform jobs, K8s CronJobs, collectors, high-volume consumers |
| Node.js | BullMQ, Agenda, `node-cron`, scheduled AWS Lambda | Email / SMS / push queues, webhook processing, serverless schedules, third-party API sync |
| Rust | `tokio` tasks, `tokio-cron-scheduler`, `rdkafka` | High-throughput data pipelines, stream processing, long-lived daemons, edge collectors |

## Packages

| Package | Purpose | Status |
|---|---|---|
| `RunSignal.Core` | Workloads, runs, items, heartbeats. No dependencies beyond the BCL | ✅ Working |
| `RunSignal.DotNet` | `AddRunSignal()`, `AddWorkload()`, keyed injection | ✅ Working |
| `RunSignal.OpenTelemetry` | `AddRunSignalInstrumentation()` for metrics and traces | ✅ Working |
| `RunSignal.Hangfire` / `RunSignal.Quartz` | Automatic job instrumentation | Planned |
| `RunSignal.NewRelic` / `RunSignal.Cli` | Alert provisioning (`runsignal diff` / `provision`) | Planned |
| `RunSignal.Java` / `.Node` / `.Go` / `.Rust` | SDKs for other languages | Planned |

The packages are not on NuGet yet.

## Quick start (.NET)

```csharp
builder.Services.AddRunSignal()
    .AddWorkload("stock-sync", o => o.ExpectEvery = TimeSpan.FromMinutes(5))
    .AddWorkload("nightly-invoice", o =>
    {
        o.Schedule = "0 2 * * *";
        o.MaxDuration = TimeSpan.FromMinutes(30);
        o.MinProcessed = 100;
        o.Severity = Severity.Critical;
    });

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddRunSignalInstrumentation().AddOtlpExporter())
    .WithTracing(t => t.AddRunSignalInstrumentation().AddOtlpExporter());
```

A scheduled job, recorded as one run that succeeds, fails or is cancelled:

```csharp
public class InvoiceJob([FromKeyedServices("nightly-invoice")] Workload workload)
{
    public Task RunAsync(CancellationToken ct) => workload.RunAsync(async ct =>
    {
        foreach (var order in await _orders.GetUnbilledAsync(ct))
        {
            await _invoices.CreateAsync(order, ct);
            workload.Processed();
        }
    }, ct);
}
```

A long-running loop, where a heartbeat on every pass reveals a loop that is alive but stuck:

```csharp
public class StockSyncWorker([FromKeyedServices("stock-sync")] Workload workload) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _client.SyncAsync(ct);
            workload.Beat();
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
```

A failure without an exception, such as a missing input file:

```csharp
var run = workload.Start();
var file = await _sftp.TryGetTodayStatementAsync();
if (file is null)
{
    run.Fail("statement_missing", "No bank statement for today");
    return;
}
workload.Processed(await _importer.ImportAsync(file));
run.Succeed();
```

Without dependency injection, use `WorkloadRegistry.Shared.Get("name")` once and keep the result.

A runnable example that prints the signals to the console is in
[samples/RunSignal.Samples.Worker](samples/RunSignal.Samples.Worker): `dotnet run --project samples/RunSignal.Samples.Worker`.

## Signals

| Metric | Attributes |
|---|---|
| `runsignal.workload.runs` | `runsignal.workload`, `runsignal.outcome` (`success` / `failure` / `cancelled`), `error.type` |
| `runsignal.workload.duration` (s) | `runsignal.workload`, `runsignal.outcome` |
| `runsignal.workload.items` | `runsignal.workload`, `runsignal.item.result` (`processed` / `failed`) |
| `runsignal.workload.heartbeats` | `runsignal.workload` |
| `runsignal.workload.last_success` (Unix s) | `runsignal.workload` |

Each run is also an `INTERNAL` span named after the workload. A process that has died cannot report that it
is missing, so RunSignal never evaluates alerts in-process: the backend detects missed and stalled work from
these signals. The full contract is in [spec/README.md](spec/README.md).

## Performance

Measured with BenchmarkDotNet on .NET 10 (Apple Silicon); run them with
`dotnet run -c Release --project benchmarks/RunSignal.Benchmarks`.

| Operation | Time | Allocated |
|---|---|---|
| `Start()` + `Succeed()` | ~35–40 ns | 0 B |
| `RunAsync` (synchronously completing work) | ~42 ns | 0 B |
| `Processed()` / `Beat()` | ~1–6 ns | 0 B |

The times include a metrics listener but not the exporter's own aggregation, which the OpenTelemetry SDK adds
on top. Spans allocate only when a tracer samples them.

## Building

```bash
dotnet test
```

Requires the .NET 8 and .NET 10 SDKs. The libraries target `net8.0`.

## License

MIT
