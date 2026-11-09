//! Gauges on the global meter provider when it is installed after the registry and its workloads. In its own
//! test binary because it sets the process-wide provider.

use std::collections::HashMap;
use std::time::Duration;

use opentelemetry::global;
use opentelemetry_sdk::metrics::data::{AggregatedMetrics, MetricData};
use opentelemetry_sdk::metrics::{
    InMemoryMetricExporter, InMemoryMetricExporterBuilder, PeriodicReader, SdkMeterProvider,
    Temporality,
};
use runsignal::{Options, Registry};

fn install() -> (InMemoryMetricExporter, SdkMeterProvider) {
    let exporter = InMemoryMetricExporterBuilder::new()
        .with_temporality(Temporality::Delta)
        .build();
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(exporter.clone()).build())
        .build();
    global::set_meter_provider(provider.clone());
    (exporter, provider)
}

// Gauge values by metric name, then by workload.
fn gauges(
    exporter: &InMemoryMetricExporter,
    provider: &SdkMeterProvider,
) -> HashMap<String, HashMap<String, f64>> {
    provider.force_flush().unwrap();
    let mut out: HashMap<String, HashMap<String, f64>> = HashMap::new();
    for rm in exporter.get_finished_metrics().unwrap() {
        for sm in rm.scope_metrics() {
            for metric in sm.metrics() {
                if let AggregatedMetrics::F64(MetricData::Gauge(g)) = metric.data() {
                    for dp in g.data_points() {
                        let workload = dp
                            .attributes()
                            .find(|kv| kv.key.as_str() == "runsignal.workload")
                            .map(|kv| kv.value.to_string())
                            .unwrap();
                        out.entry(metric.name().to_string())
                            .or_default()
                            .insert(workload, dp.value());
                    }
                }
            }
        }
    }
    exporter.reset();
    out
}

#[test]
fn workloads_registered_before_the_provider_report_their_gauges_without_recording() {
    // Created before the provider is set, as a static or an early call would be, and never run.
    let registry = Registry::global();
    registry
        .workload("early-nightly", Options::new().schedule("0 2 * * *"))
        .unwrap();
    registry
        .workload(
            "early-bounded",
            Options::new().max_duration(Duration::from_secs(60)),
        )
        .unwrap();

    let (exporter, provider) = install();
    registry.refresh_meter_provider();
    let m = gauges(&exporter, &provider);
    assert!(m["runsignal.workload.since_success"].contains_key("early-nightly"));
    assert!(!m["runsignal.workload.since_success"].contains_key("early-bounded"));
    assert_eq!(
        m["runsignal.workload.active_run_age"],
        HashMap::from([("early-bounded".to_string(), 0.0)])
    );

    // Once the provider has collected the gauges the registry keeps them, and refreshing again adds none.
    registry.refresh_meter_provider();
    registry.refresh_meter_provider();
    let m = gauges(&exporter, &provider);
    assert_eq!(m["runsignal.workload.since_success"].len(), 1);
    assert_eq!(m["runsignal.workload.active_run_age"].len(), 1);

    // A registry created after the provider reports its gauges as soon as a workload is registered.
    let later = Registry::global();
    later
        .workload("late-nightly", Options::new().schedule("0 3 * * *"))
        .unwrap();
    let m = gauges(&exporter, &provider);
    assert!(m["runsignal.workload.since_success"].contains_key("late-nightly"));
}
