# RunSignal for Rust

[![crates.io](https://img.shields.io/crates/v/runsignal)](https://crates.io/crates/runsignal)
[![docs.rs](https://img.shields.io/docsrs/runsignal)](https://docs.rs/runsignal)

**Cron job and background job monitoring for Rust, on OpenTelemetry.** Get an alert when a scheduled job is
missed, fails or overruns, or when a tokio task, consumer or daemon stops making progress.

Alerts for Rust background work that no HTTP status code describes: scheduled jobs, consumers, data pipelines
and long-lived daemons. RunSignal records runs, items and heartbeats as OpenTelemetry metrics, with the same names
as every other RunSignal SDK, so the `runsignal` CLI provisions the same alerts for Rust services as for .NET, Go,
Java and Node.js ones. The [main README](https://github.com/gokhanertogan/RunSignal#readme) covers the concepts,
the alerts and the backends.

## Installation

```bash
cargo add runsignal
```

The minimum supported Rust version is 1.75, the same as `opentelemetry` 0.33. The only dependencies are the
`opentelemetry` API and `unicode-normalization`.

## Quick start

```rust
use std::time::Duration;
use runsignal::{Options, Registry, Severity};

let registry = Registry::global();
let invoices = registry.workload("nightly-invoice", Options::new()
    .schedule("0 2 * * *")
    .max_duration(Duration::from_secs(30 * 60))
    .min_processed(100)
    .severity(Severity::Critical))?;

invoices.run(|| {
    for order in unbilled_orders()? {
        create_invoice(&order)?;
        invoices.processed(1);
    }
    Ok::<_, anyhow::Error>(())
})?;
```

`Registry::global()` reads the global meter provider on first use, not when it is created: a meter taken before
`global::set_meter_provider` stays a no-op for good. So create registries freely, but record only after the
SDK is set up. `Registry::with_meter(meter)` takes an explicit meter.

## Recording work

| Call | Records |
|---|---|
| `w.run(\|\| ...)` | One run. It succeeds on `Ok` and fails on `Err`; the result passes through. The run's span is current inside. |
| `w.run_async(future).await` | The same for async work. A future dropped before it completes (shutdown, timeout) records a cancelled run. |
| `let run = w.start()` then `run.succeed()` / `fail(&err)` / `fail_code(code, msg)` / `cancel()` | A run ended by hand. Each takes the run by value, so it cannot be recorded twice. |
| `w.processed(n)` / `w.item_failed(n)` | Items handled. |
| `w.beat()` | A heartbeat from a long-running loop. |

`error.type` is the compiler's name for the error type (`std::any::type_name`), which can change between Rust
versions: `std::io::Error` is reported as `core::io::error::Error` since Rust 1.9x. Use `fail_code` when
dashboards or alerts filter on specific errors. The `failed` and failure-rate alerts do not depend on it.

## Manifest

```rust
registry.write_manifest_from_env(Some("billing-service"))?; // writes to RUNSIGNAL_MANIFEST_PATH when it is set
```

## Exporting

**New Relic** needs delta temporality. With cumulative, a stopped workload never looks stopped:

```rust
let exporter = opentelemetry_otlp::MetricExporter::builder()
    .with_http()
    .with_endpoint("https://otlp.eu01.nr-data.net:4318/v1/metrics")   // US: https://otlp.nr-data.net:4318/v1/metrics
    .with_headers(HashMap::from([("api-key".to_string(), license_key)]))
    .with_temporality(Temporality::Delta)
    .build()?;
```

**Prometheus 3** receives OTLP at `/api/v1/otlp/v1/metrics` with the default (cumulative) temporality. Generate
its rules with `runsignal export prometheus`.

A complete program is in
[examples/worker.rs](https://github.com/gokhanertogan/RunSignal/blob/main/src/RunSignal.Rust/examples/worker.rs):
`cargo run --example worker`.

## Performance

Measured on Apple Silicon with Rust 1.99 and the OpenTelemetry SDK attached; the times include its aggregation.
Run `cargo run --release --example bench`.

| Operation | Time |
|---|---|
| `start()` + `succeed()` without tracing | ~190 ns |
| `processed()` / `beat()` | ~50 ns |

For workloads that run thousands of times per second, use `Registry::global().without_tracing()`.

## Links

[Specification](https://github.com/gokhanertogan/RunSignal/blob/main/spec/README.md) ·
[Changelog](https://github.com/gokhanertogan/RunSignal/blob/main/CHANGELOG.md) ·
[Other languages](https://github.com/gokhanertogan/RunSignal#quick-start) ·
[docs.rs](https://docs.rs/runsignal)
