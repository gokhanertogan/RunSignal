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

> Status: early development. The .NET packages (core, dependency injection, OpenTelemetry, Hangfire and
> Quartz.NET) work; New Relic provisioning, the CLI and other languages are next.
> See [docs/ROADMAP.md](docs/ROADMAP.md).

## Why RunSignal

- **Lives in the stack you already have.** Signals are plain OpenTelemetry, so they go to New Relic, Grafana,
  Datadog or any OTLP backend. There is no new SaaS to sign up for.
- **Alerts as code.** Workload expectations (schedule, max duration, heartbeat interval, minimum output) live
  next to the code, and RunSignal provisions the matching alerts.
- **Knows your scheduler.** Hangfire and Quartz.NET jobs are monitored without any code in the jobs, and their cron schedules become missed-run expectations.
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
| `RunSignal.Hangfire` | Monitors every Hangfire job (Hangfire 1.8+) | ✅ Working |
| `RunSignal.Quartz` | Monitors every Quartz.NET job (Quartz 3.x; 4.x planned) | ✅ Working |
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

## Hangfire

```csharp
builder.Services.AddRunSignal().AddHangfire();   // discovers recurring jobs at startup
builder.Services.AddHangfire((sp, config) => config
    .UseInMemoryStorage()                         // your storage
    .UseRunSignal(sp));                           // every job run is now a workload run
```

That is all: success, failure (with the job's real exception type), cancellation on shutdown and duration are
recorded for every job. Recurring jobs are named after their ID (`"Nightly Invoice"` becomes
`nightly-invoice`), and their cron expression becomes the workload's schedule. Other jobs are named after their
class and method (`InvoiceJobs.Generate` becomes `invoice-jobs.generate`).

Name a job and set its expectations with `[Workload]`, and count items through `PerformContext`:

```csharp
[Workload("nightly-invoice", MaxDuration = "30m", MinProcessed = 100, Severity = Severity.Critical)]
public async Task GenerateInvoices(PerformContext? context = null)
{
    foreach (var order in await _orders.GetUnbilledAsync())
    {
        await _invoices.CreateAsync(order);
        context.GetWorkload()?.Processed();
    }
}
```

Durations accept `90s`, `30m`, `2h`, `1d` or `hh:mm:ss`. Options declared with `AddWorkload` for the same name
take precedence over the attribute.

## Quartz.NET

```csharp
builder.Services.AddRunSignal().AddQuartz();     // discovers scheduled jobs at startup
builder.Services.AddQuartz(q => q.AddJobListener<RunSignalJobListener>());
```

Or, without dependency injection: `scheduler.UseRunSignal(WorkloadRegistry.Shared)` and
`await scheduler.DiscoverRunSignalWorkloadsAsync(WorkloadRegistry.Shared)`.

Jobs are named after their key (`reports.DailySales` becomes `reports.daily-sales`; the `DEFAULT` group is left
out; a job scheduled without an identity, whose key is a random GUID, is named after its class), and a cron
trigger's expression becomes the schedule. `[Workload]` works on the job class, and
`context.GetWorkload()?.Processed()` counts items. Quartz.NET 4 changed its job and listener interfaces, so
`RunSignal.Quartz` currently supports Quartz.NET 3.x.

Workload names are derived readably from any identifier, including non-ASCII ones: `e-Arşiv gönderimi` becomes
`e-arsiv-gonderimi`.

`AddHangfire()` and `AddQuartz()` register jobs as workloads as soon as the application starts, not when they
first run. A job that never starts is exactly what the `missed` alert is for, so it has to be known before
it runs.

## Workload manifest

The manifest lists every workload of a service and its expectations. The CLI will provision alerts from it
([format](spec/manifest.schema.json)):

```json
{
  "version": 1,
  "service": "billing-service",
  "workloads": [
    { "name": "nightly-invoice", "schedule": "0 2 * * *", "maxDurationSeconds": 1800, "minProcessed": 100, "severity": "critical" },
    { "name": "stock-sync", "expectEverySeconds": 300, "failureRateThreshold": 0.2, "severity": "warning" }
  ]
}
```

The application writes it once it has started and discovered its jobs. Turn it on with an environment
variable, with no code change:

```bash
RUNSIGNAL_MANIFEST_PATH=artifacts/runsignal.json dotnet run
```

You can also set it in code: `AddRunSignal(o => { o.ManifestPath = "runsignal.json"; o.ServiceName = "billing-service"; })`.
The service name defaults to `OTEL_SERVICE_NAME`, then the entry assembly name, and must match the
`service.name` your signals are exported with. In code, use `registry.CreateManifest("billing-service").ToJson()`.
The manifest comes from the running application, not the build, because schedules such as Hangfire recurring
jobs exist only at runtime.

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
