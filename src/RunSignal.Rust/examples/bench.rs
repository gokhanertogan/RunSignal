//! Hot-path cost with the OpenTelemetry metrics SDK attached; the times include its aggregation.
//!
//!     cargo run --release --example bench

use std::time::Instant;

use opentelemetry::metrics::MeterProvider;
use opentelemetry_sdk::metrics::{InMemoryMetricExporter, PeriodicReader, SdkMeterProvider};
use runsignal::{Options, Registry};

fn bench(name: &str, iterations: u32, mut f: impl FnMut()) {
    for _ in 0..10_000 {
        f();
    }
    let start = Instant::now();
    for _ in 0..iterations {
        f();
    }
    println!(
        "{name:<30} {:>6.0} ns/op",
        start.elapsed().as_nanos() as f64 / iterations as f64
    );
}

fn main() {
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(InMemoryMetricExporter::default()).build())
        .build();
    let registry = Registry::with_meter(provider.meter("RunSignal")).without_tracing();
    let workload = registry.workload("benchmark", Options::new()).unwrap();

    bench("start + succeed (tracing off)", 1_000_000, || {
        workload.start().succeed()
    });
    bench("run (tracing off)", 1_000_000, || {
        workload.run(|| Ok::<_, std::io::Error>(())).unwrap()
    });
    bench("processed", 2_000_000, || workload.processed(1));
    bench("beat", 2_000_000, || workload.beat());
}
