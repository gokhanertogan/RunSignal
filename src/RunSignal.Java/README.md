# RunSignal for Java

Monitor Java background work that no HTTP status code describes, such as Spring `@Scheduled` jobs, consumers,
batches and sync loops, with OpenTelemetry. It emits the same signals and manifest as every other RunSignal SDK,
so the `runsignal` CLI provisions the same New Relic alerts for Java services as for .NET, Go and Node.js ones.
See the [main README](../../README.md) for the concepts and the alerts.

| Artifact | Purpose |
|---|---|
| `io.github.gokhanertogan.runsignal:runsignal` | Core: workloads, runs, items, heartbeats, manifest. Depends only on `opentelemetry-api`. |
| `io.github.gokhanertogan.runsignal:runsignal-spring-boot-starter` | Monitors every `@Scheduled` method with no code in your jobs. |

The artifacts are not on Maven Central yet; build them with `mvn install`. Requires Java 17 or later. The starter is tested against Spring Boot 3.2, 3.5 and 4.1 (Spring Framework 6.1, 6.2
and 7.0; Spring Batch 5.1, 5.2 and 6.0). It relies on the `@Scheduled` observations Spring added in Framework 6.1, so earlier versions are not
supported.

## Spring Boot

Add the starter. That is all: every `@Scheduled` method is recorded, cron jobs are registered as workloads as
soon as the application is ready (so a job that never fires gets a `missed` alert), and the cron expression
becomes the schedule.

```java
@Scheduled(cron = "0 0 2 * * *")
@RunSignal(value = "nightly-invoice", maxDuration = "30m", minProcessed = 100, severity = Severity.CRITICAL)
void generateInvoices() { ... }

@Scheduled(fixedDelay = 60_000)
void syncStock() { ... }   // recorded as "jobs.sync-stock" (class and method name)
```

- **How runs are recorded:** through Spring's own scheduling observations. No proxies are involved, so final classes and self-invocation work.
- **Cron expressions:** placeholders (`cron = "${jobs.cleanup.cron}"`) and macros (`@daily`) are resolved.
- **OpenTelemetry instance:** the registry uses your `OpenTelemetry` bean if there is one, otherwise `GlobalOpenTelemetry` (for example from the Java agent).

```yaml
runsignal:
  manifest-path: build/runsignal.json   # or set RUNSIGNAL_MANIFEST_PATH
  service-name: billing-service         # defaults to spring.application.name
  tracing: true                         # a span per run
```

Inject `WorkloadRegistry` for workloads that are not `@Scheduled` methods.

### Spring Batch

Spring Batch jobs are monitored with no code either.

- **Runs:** every job execution is a run of a workload named after the job (`invoiceExport` becomes `invoice-export`). `COMPLETED` is a success, `FAILED` is a failure with the job's first exception (or its exit code), and `STOPPED` and `ABANDONED` are cancelled.
- **Items:** items written by the job's steps count as processed, and skipped items as failed, so `minProcessed` and the item failure rate work out of the box.
- **Registration:** jobs are registered as workloads at startup.
- **Versions:** Spring Batch 6 moved its types to new packages. The starter works with Spring Batch 5 and 6 through reflection, with a few reflective calls per job execution, and is tested with both.

Set expectations for jobs, or for any workload, in configuration. These take precedence over `@RunSignal`, so
they can differ per environment:

```yaml
runsignal:
  workloads:
    invoice-export:
      schedule: "0 0 2 * * *"
      max-duration: 30m
      min-processed: 100
      severity: critical
```

## Core API

```java
WorkloadRegistry registry = new WorkloadRegistry(openTelemetry);   // or WorkloadRegistry.global()

Workload invoices = registry.workload("nightly-invoice", WorkloadOptions.builder()
    .schedule("0 2 * * *")
    .maxDuration("30m")
    .minProcessed(100)
    .severity(Severity.CRITICAL)
    .build());

// One run: succeeds when it returns, is cancelled on interruption or cancellation, fails when it throws.
invoices.run(() -> {
  for (Order order : unbilledOrders()) {
    createInvoice(order);
    invoices.processed();
  }
});
```

A long-running loop sends heartbeats with `workload.beat()`. A manual run is ended with `succeed()`,
`fail(e)`, `failCode("statement_missing", "...")` or `cancel()`. For failed runs, `error.type` is the
exception's class name, looking through wrappers such as `ExecutionException`. Implement `ErrorTyped` to
supply a stable code instead.

`WorkloadRegistry.global()` reads `GlobalOpenTelemetry` on first use, not when it is created. Calling
`GlobalOpenTelemetry.get()` before the SDK or the agent registers would fix it to a no-op for good.

Write the manifest with `registry.writeManifestFromEnv("billing-service")` once every workload is registered.
The starter does this for you.

### Exporting to New Relic

Use delta temporality. With cumulative, a stopped workload never looks stopped:

```java
OtlpGrpcMetricExporter.builder()
    .setEndpoint("https://otlp.eu01.nr-data.net:4317")   // US: https://otlp.nr-data.net:4317
    .addHeader("api-key", licenseKey)
    .setAggregationTemporalitySelector(AggregationTemporalitySelector.deltaPreferred())
    .build();
```

Or set `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta`. A complete Spring Boot application is in
[examples/worker](examples/worker).

## Building

```bash
mvn install
```
