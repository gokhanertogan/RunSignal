# RunSignal for Rust

Monitor Rust background work that no HTTP status code describes, such as scheduled jobs, consumers, data
pipelines and long-lived daemons, with OpenTelemetry. It emits the same signals and manifest as every other
RunSignal SDK, so the `runsignal` CLI provisions the same New Relic alerts for Rust services as for .NET, Go,
Java and Node.js ones. See the [main README](../../README.md) for the concepts and the alerts.

```toml
[dependencies]
runsignal = { git = "https://github.com/gokhanertogan/RunSignal" }
```

The minimum supported Rust version is 1.75, the same as `opentelemetry` 0.33. The only dependencies are
`opentelemetry` (API only) and `unicode-normalization`.

## Usage

```rust
use std::time::Duration;
use runsignal::{Options, Registry, Severity};

let registry = Registry::global();
let invoices = registry.workload("nightly-invoice", Options::new()
    .schedule("0 2 * * *")
    .max_duration(Duration::from_secs(30 * 60))
    .min_processed(100)
    .severity(Severity::Critical))?;

// One run: succeeds on Ok, fails on Err. The result passes through.
invoices.run(|| {
    for order in unbilled_orders()? {
        create_invoice(&order)?;
        invoices.processed(1);
    }
    Ok::<_, anyhow::Error>(())
})?;

// Async: a future dropped before it completes (shutdown, timeout) records a cancelled run.
invoices.run_async(async { generate().await }).await?;
```

A long-running loop sends heartbeats with `workload.beat()`. A manual run is started with `workload.start()`
and ended with `succeed()`, `fail(&err)`, `fail_code("statement_missing", "...")` or `cancel()`. Each takes the
run by value, so a run cannot be recorded twice.

- **Error types:** `error.type` is the compiler's name for the error type (`std::any::type_name`), which can change between Rust versions. For example, `std::io::Error` is reported as `core::io::error::Error` since Rust 1.9x. Use `fail_code` when dashboards or alerts filter on specific errors. The `failed` and failure-rate alerts do not depend on it.
- **Global provider:** `Registry::global()` reads the global meter provider on first use, not when it is created. A meter taken before `global::set_meter_provider` stays a no-op for good, so create registries freely but record only after the SDK is set up. `Registry::with_meter(meter)` takes an explicit meter.
- **Tracing:** for workloads that run thousands of times per second, use `Registry::global().without_tracing()`.

Write the manifest with `registry.write_manifest_from_env(Some("billing-service"))?` once every workload is
registered. It writes to `RUNSIGNAL_MANIFEST_PATH` when that is set.

### Exporting to New Relic

Use delta temporality. With cumulative, a stopped workload never looks stopped:

```rust
let exporter = opentelemetry_otlp::MetricExporter::builder()
    .with_http()
    .with_endpoint("https://otlp.eu01.nr-data.net:4318/v1/metrics")   // US: https://otlp.nr-data.net:4318/v1/metrics
    .with_headers(HashMap::from([("api-key".to_string(), license_key)]))
    .with_temporality(Temporality::Delta)
    .build()?;
```

A complete program is in [examples/worker.rs](examples/worker.rs): `cargo run --example worker`.

## Performance

Measured on Apple Silicon with Rust 1.99 and the OpenTelemetry SDK attached. The times include the SDK's
aggregation. Run with `cargo run --release --example bench`.

| Operation | Time |
|---|---|
| `start()` + `succeed()` without tracing | ~190 ns |
| `processed()` / `beat()` | ~50 ns |
