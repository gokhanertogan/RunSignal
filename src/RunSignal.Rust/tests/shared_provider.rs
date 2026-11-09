//! Registries recording on one meter provider. OpenTelemetry SDKs may keep only the first instrument registered
//! under a name, or export a duplicate as a second stream; every registry must still report its own workloads,
//! in one stream per metric. In its own test binary because it sets the process-wide provider.

use std::time::Duration;

use opentelemetry::global;
use opentelemetry::metrics::MeterProvider;
use opentelemetry_sdk::metrics::data::{AggregatedMetrics, MetricData};
use opentelemetry_sdk::metrics::{
    InMemoryMetricExporter, InMemoryMetricExporterBuilder, PeriodicReader, SdkMeterProvider,
};
use runsignal::{Options, Registry};

fn provider() -> (InMemoryMetricExporter, SdkMeterProvider) {
    let exporter = InMemoryMetricExporterBuilder::new().build();
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(exporter.clone()).build())
        .build();
    (exporter, provider)
}

fn expectations() -> Options {
    Options::new()
        .schedule("0 2 * * *")
        .max_duration(Duration::from_secs(600))
}

fn assert_both_workloads(
    exporter: &InMemoryMetricExporter,
    provider: &SdkMeterProvider,
    first: &str,
    second: &str,
) {
    provider.force_flush().unwrap();
    let finished = exporter.get_finished_metrics().unwrap();
    assert_eq!(finished.len(), 1, "one collection");
    let metrics: Vec<_> = finished[0]
        .scope_metrics()
        .flat_map(|sm| sm.metrics())
        .collect();
    let mut names: Vec<&str> = metrics.iter().map(|m| m.name()).collect();
    names.sort_unstable();
    let count = names.len();
    names.dedup();
    assert_eq!(names.len(), count, "a metric is exported as two streams");

    for name in [
        "runsignal.workload.since_success",
        "runsignal.workload.active_run_age",
    ] {
        let streams: Vec<_> = metrics.iter().filter(|m| m.name() == name).collect();
        assert_eq!(streams.len(), 1, "{name} streams");
        let AggregatedMetrics::F64(MetricData::Gauge(gauge)) = streams[0].data() else {
            panic!("{name} is not a gauge");
        };
        let mut workloads: Vec<String> = gauge
            .data_points()
            .map(|dp| {
                dp.attributes()
                    .find(|kv| kv.key.as_str() == "runsignal.workload")
                    .map(|kv| kv.value.to_string())
                    .unwrap()
            })
            .collect();
        workloads.sort();
        assert_eq!(workloads, [first, second], "{name} workloads");
    }
}

#[test]
fn registries_on_one_provider_both_report_their_gauges() {
    // An explicit provider, shared by two registries.
    let (exporter, provider) = self::provider();
    let a = Registry::with_meter(provider.meter("RunSignal"));
    let b = Registry::with_meter(provider.meter("RunSignal")).without_tracing();
    a.workload("first", expectations()).unwrap();
    b.workload("second", expectations()).unwrap();
    assert_both_workloads(&exporter, &provider, "first", "second");

    // The global provider, shared by a registry created before it was installed and one created after.
    let early = Registry::global();
    early.workload("global-early", expectations()).unwrap();
    let (exporter, provider) = self::provider();
    global::set_meter_provider(provider.clone());
    early.refresh_meter_provider();
    let late = Registry::global();
    late.workload("global-late", expectations()).unwrap();
    assert_both_workloads(&exporter, &provider, "global-early", "global-late");
}
