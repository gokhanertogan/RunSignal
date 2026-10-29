# RunSignal for .NET

[![NuGet](https://img.shields.io/nuget/v/RunSignal.DotNet)](https://www.nuget.org/packages/RunSignal.DotNet)

**Hangfire, Quartz.NET and BackgroundService monitoring, on OpenTelemetry.** Get an alert when a recurring job
is missed, fails, overruns or processes nothing, or when a worker loop stalls, with no code in your jobs.

Alerts for .NET background work that no HTTP status code describes: Hangfire and Quartz.NET jobs,
`BackgroundService` loops and consumers. RunSignal records runs, items and heartbeats as OpenTelemetry metrics,
with the same names as every other RunSignal SDK. The `runsignal` CLI turns them into New Relic alerts and
dashboards, or Prometheus rules and a Grafana dashboard. The
[main README](https://github.com/gokhanertogan/RunSignal#readme) covers the concepts, the alerts and the backends.

## Installation

```bash
dotnet add package RunSignal.DotNet           # registry, dependency injection, manifest
dotnet add package RunSignal.OpenTelemetry    # AddRunSignalInstrumentation()
dotnet add package RunSignal.Hangfire         # optional: Hangfire 1.8+
dotnet add package RunSignal.Quartz           # optional: Quartz.NET 3 (RunSignal.Quartz4 for Quartz.NET 4)
dotnet tool install -g RunSignal.Cli          # the runsignal CLI, for CI
```

The packages target `net8.0`; `RunSignal.Quartz4` targets `net10.0`, like Quartz.NET 4.

| Package | Purpose |
|---|---|
| `RunSignal.Core` | Workloads, runs, items, heartbeats, manifest. No dependencies beyond the BCL. |
| `RunSignal.DotNet` | `AddRunSignal()`, `AddWorkload()`, keyed injection, startup discovery, manifest writing. |
| `RunSignal.OpenTelemetry` | `AddRunSignalInstrumentation()` for metrics and traces, with duration buckets for jobs. |
| `RunSignal.Hangfire` | Monitors every Hangfire 1.8+ job. |
| `RunSignal.Quartz` / `RunSignal.Quartz4` | Monitors every Quartz.NET 3 / Quartz.NET 4 job. |

## Setup

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

Inject a workload with `[FromKeyedServices("nightly-invoice")] Workload workload`. Without dependency injection,
call `WorkloadRegistry.Shared.Get("name")` once and keep the result.

## Recording work

A scheduled job, recorded as one run that succeeds, fails or is cancelled (when its token is cancelled):

```csharp
public Task RunAsync(CancellationToken ct) => workload.RunAsync(async ct =>
{
    foreach (var order in await _orders.GetUnbilledAsync(ct))
    {
        await _invoices.CreateAsync(order, ct);
        workload.Processed();
    }
}, ct);
```

A long-running loop, where a heartbeat on every pass reveals a loop that is alive but stuck:

```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await _client.SyncAsync(ct);
        workload.Beat();
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
    }
}
```

A failure without an exception, such as a missing input file:

```csharp
var run = workload.Start();
if (await _sftp.TryGetTodayStatementAsync() is not { } file)
{
    run.Fail("statement_missing", "No bank statement for today");
    return;
}
workload.Processed(await _importer.ImportAsync(file));
run.Succeed();
```

`error.type` is the exception's full type name, or the code passed to `Fail`. `WorkloadRun` is a struct, so
starting a run does not allocate; ending it twice records it twice, so prefer `RunAsync`.

## Hangfire

```csharp
builder.Services.AddRunSignal().AddHangfire();   // registers recurring jobs at startup
builder.Services.AddHangfire((sp, config) => config
    .UseSqlServerStorage(connectionString)       // your storage
    .UseRunSignal(sp));                           // records every job run
```

- **What is recorded:** for every job, success, failure (with the job's own exception type, unwrapped from Hangfire's wrapper), cancellation on shutdown and duration.
- **Naming:** recurring jobs are named after their ID (`"Nightly Invoice"` becomes `nightly-invoice`), and their cron expression becomes the schedule. Other jobs are named after their class and method (`InvoiceJobs.Generate` becomes `invoice-jobs.generate`).
- **`[Workload]`:** sets the name and expectations; count items through `PerformContext`:

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
builder.Services.AddRunSignal().AddQuartz();     // registers scheduled jobs at startup

// Quartz.NET 3 (RunSignal.Quartz)
builder.Services.AddQuartz(q => q.AddJobListener<RunSignalJobListener>());

// Quartz.NET 4 (RunSignal.Quartz4, .NET 10): listeners take matchers
builder.Services.AddQuartz(q => q.AddJobListener<RunSignalJobListener>([GroupMatcher<JobKey>.AnyGroup()]));
```

Without dependency injection: `scheduler.UseRunSignal(WorkloadRegistry.Shared)` and
`await scheduler.DiscoverRunSignalWorkloadsAsync(WorkloadRegistry.Shared)`.

- **Naming:** jobs are named after their key (`reports.DailySales` becomes `reports.daily-sales`; the `DEFAULT` group is left out). A job scheduled without an identity, whose key is a random GUID, is named after its class.
- **Schedules:** a cron trigger's expression becomes the schedule. It is converted from Quartz's format (1 = Sunday) to the standard one.
- **`[Workload]`** works on the job class, and `context.GetWorkload()?.Processed()` counts items.
- **Quartz.NET 4** changed its interfaces to `ValueTask` and supports only .NET 10. `RunSignal.Quartz4` is built from the same sources and uses the same namespace, so upgrading only changes the package. It supports Quartz 4.0 and later and is tested against 4.6, including named schedulers.

## Manifest

The application writes its manifest once it has started and discovered its jobs, when
`RUNSIGNAL_MANIFEST_PATH` is set:

```bash
RUNSIGNAL_MANIFEST_PATH=artifacts/runsignal.json dotnet run
```

You can also set it in code: `AddRunSignal(o => { o.ManifestPath = "runsignal.json"; o.ServiceName = "billing-service"; })`.
The service name defaults to `OTEL_SERVICE_NAME`, then the entry assembly name, and must match the
`service.name` the signals are exported with. Without dependency injection, call
`registry.CreateManifest("billing-service").ToJson()`.

## Exporting

**New Relic** needs delta temporality. With cumulative, a stopped workload never looks stopped:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("billing-service"))      // must match the manifest's service
    .WithMetrics(m => m
        .AddRunSignalInstrumentation()
        .AddOtlpExporter((exporter, reader) =>
        {
            exporter.Endpoint = new Uri("https://otlp.eu01.nr-data.net");  // US: https://otlp.nr-data.net
            exporter.Headers = $"api-key={licenseKey}";                    // an ingest (license) key
            reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
        }));
```

**Prometheus 3** receives OTLP at `/api/v1/otlp/v1/metrics` with the default (cumulative) temporality. Generate
its rules with `runsignal export prometheus`.

## Performance

Measured with BenchmarkDotNet on .NET 10 (Apple Silicon), with a metrics listener but without the
OpenTelemetry SDK's own aggregation: `dotnet run -c Release --project benchmarks/RunSignal.Benchmarks`.

| Operation | Time | Allocated |
|---|---|---|
| `Start()` + `Succeed()` | ~35–40 ns | 0 B |
| `RunAsync` (synchronously completing work) | ~42 ns | 0 B |
| `Processed()` | ~1–6 ns | 0 B |
| `Beat()` | ~14 ns | 0 B |

Spans allocate only when a tracer samples them.

## Samples

- [samples/RunSignal.Samples.Worker](https://github.com/gokhanertogan/RunSignal/tree/main/samples/RunSignal.Samples.Worker) prints the signals to the console. It sends them to New Relic when `NEW_RELIC_LICENSE_KEY` is set, and to any OTLP/HTTP endpoint when `RUNSIGNAL_OTLP_ENDPOINT` is set.
- [samples/RunSignal.Samples.AlertDrill](https://github.com/gokhanertogan/RunSignal/tree/main/samples/RunSignal.Samples.AlertDrill) breaks one expectation per workload to check every alert end to end.

## Links

[Specification](https://github.com/gokhanertogan/RunSignal/blob/main/spec/README.md) · [Changelog](https://github.com/gokhanertogan/RunSignal/blob/main/CHANGELOG.md) ·
[Other languages](https://github.com/gokhanertogan/RunSignal#quick-start)
