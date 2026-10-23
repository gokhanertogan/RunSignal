# RunSignal for Java

Alerts for Java background work that no HTTP status code describes: Spring `@Scheduled` jobs, Spring Batch jobs,
consumers and sync loops. RunSignal records runs, items and heartbeats as OpenTelemetry metrics, with the same
names as every other RunSignal SDK, so the `runsignal` CLI provisions the same alerts for Java services as for
.NET, Go, Node.js and Rust ones. The [main README](../../README.md) covers the concepts, the alerts and the
backends.

## Installation

| Artifact | Use it for |
|---|---|
| `io.github.gokhanertogan.runsignal:runsignal-spring-boot-starter` | Spring Boot: monitors `@Scheduled` and Spring Batch jobs with no code in them. |
| `io.github.gokhanertogan.runsignal:runsignal` | Any Java application: the core API. Depends only on `opentelemetry-api`. |

```xml
<dependency>
  <groupId>io.github.gokhanertogan.runsignal</groupId>
  <artifactId>runsignal-spring-boot-starter</artifactId>
  <version>0.1.0</version>
</dependency>
```

```kotlin
implementation("io.github.gokhanertogan.runsignal:runsignal-spring-boot-starter:0.1.0") // Gradle
```

Requires Java 17 or later. The starter is tested against Spring Boot 3.2, 3.5 and 4.1 (Spring Framework 6.1, 6.2
and 7.0; Spring Batch 5.1, 5.2 and 6.0). It relies on the `@Scheduled` observations Spring added in Framework 6.1,
so earlier versions are not supported.

## Spring Boot

Add the starter. Every `@Scheduled` method is then recorded, cron jobs are registered as workloads as soon as
the application is ready (so a job that never fires gets a `missed` alert), and the cron expression becomes the
schedule:

```java
@Scheduled(cron = "0 0 2 * * *")
@RunSignal(value = "nightly-invoice", maxDuration = "30m", minProcessed = 100, severity = Severity.CRITICAL)
void generateInvoices() { ... }

@Scheduled(fixedDelay = 60_000)
void syncStock() { ... }   // recorded as "jobs.sync-stock" (class and method name)
```

- **How runs are recorded:** through Spring's own scheduling observations. No proxies are involved, so final classes and self-invocation work.
- **Cron expressions:** placeholders (`${jobs.cleanup.cron}`) and macros (`@daily`) are resolved.
- **OpenTelemetry instance:** the registry uses your `OpenTelemetry` bean if there is one, otherwise `GlobalOpenTelemetry` (for example from the Java agent).

```yaml
runsignal:
  manifest-path: build/runsignal.json   # or set RUNSIGNAL_MANIFEST_PATH
  service-name: billing-service         # defaults to spring.application.name
  tracing: true                         # a span per run
  workloads:                            # expectations per workload; they take precedence over @RunSignal
    invoice-export:
      schedule: "0 0 2 * * *"
      max-duration: 30m
      min-processed: 100
      severity: critical
```

Inject `WorkloadRegistry` for workloads that are not `@Scheduled` methods or Batch jobs.

### Spring Batch

Spring Batch jobs are monitored with no code either.

- **Runs:** every job execution is a run of a workload named after the job (`invoiceExport` becomes `invoice-export`). `COMPLETED` is a success, `FAILED` is a failure with the job's first exception (or its exit code), and `STOPPED` and `ABANDONED` are cancelled.
- **Items:** items written by the job's steps count as processed and skipped items as failed, so `minProcessed` and the item failure rate work out of the box.
- **Registration:** jobs are registered as workloads at startup.
- **Versions:** Spring Batch 6 moved its types to new packages. The starter supports Batch 5 and 6 through reflection, with a few reflective calls per job execution.

Set Batch jobs' expectations under `runsignal.workloads`, as above.

## Core API

```java
WorkloadRegistry registry = new WorkloadRegistry(openTelemetry);   // or WorkloadRegistry.global()

Workload invoices = registry.workload("nightly-invoice", WorkloadOptions.builder()
    .schedule("0 2 * * *")
    .maxDuration("30m")
    .minProcessed(100)
    .severity(Severity.CRITICAL)
    .build());

invoices.run(() -> {
  for (Order order : unbilledOrders()) {
    createInvoice(order);
    invoices.processed();
  }
});
```

| Call | Records |
|---|---|
| `w.run(...)` / `w.call(...)` | One run. It succeeds when it returns, is cancelled on `InterruptedException` or `CancellationException`, and fails when it throws. Exceptions are rethrown. |
| `WorkloadRun run = w.start()` then `succeed()` / `fail(e)` / `failCode(code, msg)` / `cancel()` | A run ended by hand. Later calls are ignored. |
| `w.processed(n)` / `w.itemFailed(n)` | Items handled. |
| `w.beat()` | A heartbeat from a long-running loop. |

`error.type` is the exception's class name, looking through wrappers such as `ExecutionException`. Implement
`ErrorTyped` to supply a stable code instead.

`WorkloadRegistry.global()` reads `GlobalOpenTelemetry` on first use, not when it is created, because calling
`GlobalOpenTelemetry.get()` before the SDK or the agent registers would fix it to a no-op for good. Write the
manifest with `registry.writeManifestFromEnv("billing-service")`; the starter does this for you.

## Exporting

**New Relic** needs delta temporality. With cumulative, a stopped workload never looks stopped:

```java
OtlpGrpcMetricExporter.builder()
    .setEndpoint("https://otlp.eu01.nr-data.net:4317")   // US: https://otlp.nr-data.net:4317
    .addHeader("api-key", licenseKey)
    .setAggregationTemporalitySelector(AggregationTemporalitySelector.deltaPreferred())
    .build();
```

Or set `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta`. **Prometheus 3** receives OTLP at
`/api/v1/otlp/v1/metrics` with the default (cumulative) temporality; generate its rules with
`runsignal export prometheus`.

A complete Spring Boot application is in [examples/worker](examples/worker).

## Building

```bash
mvn install                                              # Spring Boot 4 / Spring Batch 6
mvn install -Dspring-boot.version=3.5.16 -Dbatch=5       # Spring Boot 3 / Spring Batch 5
```

## Links

[Specification](../../spec/README.md) · [Changelog](../../CHANGELOG.md) · [Other languages](../../README.md#quick-start)
