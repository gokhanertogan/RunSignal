use std::collections::HashMap;
use std::sync::OnceLock;
use std::time::Duration;

use opentelemetry::global;
use opentelemetry::metrics::MeterProvider;
use opentelemetry_sdk::metrics::data::{AggregatedMetrics, MetricData};
use opentelemetry_sdk::metrics::{
    InMemoryMetricExporter, InMemoryMetricExporterBuilder, PeriodicReader, SdkMeterProvider,
    Temporality,
};
use opentelemetry_sdk::trace::{InMemorySpanExporter, SdkTracerProvider};
use runsignal::{to_workload_name, Options, Registry, Severity};

#[derive(Debug, Clone, PartialEq)]
struct Point {
    value: f64,
    attributes: HashMap<String, String>,
}

struct Harness {
    exporter: InMemoryMetricExporter,
    provider: SdkMeterProvider,
    registry: Registry,
}

// Spans go to the global tracer provider, which is set once for every test.
fn spans() -> &'static InMemorySpanExporter {
    static SPANS: OnceLock<InMemorySpanExporter> = OnceLock::new();
    SPANS.get_or_init(|| {
        let exporter = InMemorySpanExporter::default();
        global::set_tracer_provider(
            SdkTracerProvider::builder()
                .with_simple_exporter(exporter.clone())
                .build(),
        );
        exporter
    })
}

fn harness() -> Harness {
    spans();
    let exporter = InMemoryMetricExporterBuilder::new()
        .with_temporality(Temporality::Delta)
        .build();
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(exporter.clone()).build())
        .build();
    let registry = Registry::with_meter(provider.meter("RunSignal"));
    Harness {
        exporter,
        provider,
        registry,
    }
}

impl Harness {
    fn collect(&self) -> HashMap<String, Vec<Point>> {
        self.provider.force_flush().unwrap();
        let mut out: HashMap<String, Vec<Point>> = HashMap::new();
        for rm in self.exporter.get_finished_metrics().unwrap() {
            for sm in rm.scope_metrics() {
                for metric in sm.metrics() {
                    let points = out.entry(metric.name().to_string()).or_default();
                    let attrs = |kvs: &mut dyn Iterator<Item = &opentelemetry::KeyValue>| {
                        kvs.map(|kv| (kv.key.to_string(), kv.value.to_string()))
                            .collect::<HashMap<_, _>>()
                    };
                    match metric.data() {
                        AggregatedMetrics::U64(MetricData::Sum(sum)) => {
                            for dp in sum.data_points() {
                                points.push(Point {
                                    value: dp.value() as f64,
                                    attributes: attrs(&mut dp.attributes()),
                                });
                            }
                        }
                        AggregatedMetrics::F64(MetricData::Gauge(gauge)) => {
                            for dp in gauge.data_points() {
                                points.push(Point {
                                    value: dp.value(),
                                    attributes: attrs(&mut dp.attributes()),
                                });
                            }
                        }
                        AggregatedMetrics::F64(MetricData::Histogram(h)) => {
                            for dp in h.data_points() {
                                points.push(Point {
                                    value: dp.sum(),
                                    attributes: attrs(&mut dp.attributes()),
                                });
                            }
                        }
                        _ => {}
                    }
                }
            }
        }
        self.exporter.reset();
        out
    }
}

fn outcome_counts(points: &[Point]) -> HashMap<String, f64> {
    let mut out = HashMap::new();
    for p in points {
        let key = format!(
            "{}:{}",
            p.attributes
                .get("runsignal.outcome")
                .cloned()
                .unwrap_or_default(),
            p.attributes.get("error.type").cloned().unwrap_or_default()
        );
        *out.entry(key).or_insert(0.0) += p.value;
    }
    out
}

#[test]
fn run_records_outcome_duration_and_error_type() {
    let h = harness();
    let workload = h
        .registry
        .workload("nightly-invoice", Options::new())
        .unwrap();

    assert_eq!(workload.run(|| Ok::<_, std::io::Error>(42)).unwrap(), 42);
    let err = workload
        .run(|| Err::<(), _>(std::io::Error::other("bank did not answer")))
        .unwrap_err();
    assert_eq!(err.to_string(), "bank did not answer");
    workload
        .start()
        .fail_code("statement_missing", "no statement today");

    let m = h.collect();
    assert_eq!(
        outcome_counts(&m["runsignal.workload.runs"]),
        HashMap::from([
            ("success:".to_string(), 1.0),
            // The compiler's name for the type, which is not stable across Rust versions.
            (
                format!("failure:{}", std::any::type_name::<std::io::Error>()),
                1.0
            ),
            ("failure:statement_missing".to_string(), 1.0),
        ])
    );
    assert!(m["runsignal.workload.runs"]
        .iter()
        .all(|p| p.attributes["runsignal.workload"] == "nightly-invoice"));
    assert_eq!(m["runsignal.workload.duration"].len(), 2); // success and failure
}

#[tokio::test]
async fn run_async_succeeds_fails_and_is_cancelled_when_dropped() {
    let h = harness();
    let workload = h.registry.workload("price-sync", Options::new()).unwrap();

    workload
        .run_async(async { Ok::<_, std::io::Error>(()) })
        .await
        .unwrap();
    let _ = workload
        .run_async(async { Err::<(), _>(std::fmt::Error) })
        .await;
    // A timeout drops the future before it completes.
    let slow = workload.run_async(async {
        tokio::time::sleep(Duration::from_secs(10)).await;
        Ok::<_, std::io::Error>(())
    });
    assert!(tokio::time::timeout(Duration::from_millis(20), slow)
        .await
        .is_err());

    assert_eq!(
        outcome_counts(&h.collect()["runsignal.workload.runs"]),
        HashMap::from([
            ("success:".to_string(), 1.0),
            (
                format!("failure:{}", std::any::type_name::<std::fmt::Error>()),
                1.0
            ),
            ("cancelled:".to_string(), 1.0),
        ])
    );
}

#[test]
fn runs_are_traced_with_the_workload_name() {
    let h = harness();
    let workload = h.registry.workload("traced-job", Options::new()).unwrap();
    workload.run(|| Ok::<_, std::io::Error>(())).unwrap();

    let spans = spans().get_finished_spans().unwrap();
    let span = spans
        .iter()
        .find(|s| s.name == "traced-job")
        .expect("a span named after the workload");
    assert!(span
        .attributes
        .iter()
        .any(|kv| kv.key.as_str() == "runsignal.outcome" && kv.value.as_str() == "success"));
}

#[test]
fn without_tracing_records_metrics_but_no_spans() {
    let exporter = InMemoryMetricExporterBuilder::new()
        .with_temporality(Temporality::Delta)
        .build();
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(exporter.clone()).build())
        .build();
    let registry = Registry::with_meter(provider.meter("RunSignal")).without_tracing();
    let workload = registry
        .workload("untraced-consumer", Options::new())
        .unwrap();

    workload.run(|| Ok::<_, std::io::Error>(())).unwrap();
    provider.force_flush().unwrap();

    assert!(!spans()
        .get_finished_spans()
        .unwrap()
        .iter()
        .any(|s| s.name == "untraced-consumer"));
    assert!(!exporter.get_finished_metrics().unwrap().is_empty());
}

#[test]
fn items_heartbeats_and_age_gauges() {
    let h = harness();
    let scheduled = h
        .registry
        .workload("scheduled", Options::new().schedule("0 2 * * *"))
        .unwrap();
    let looping = h
        .registry
        .workload(
            "loop",
            Options::new().expect_every(Duration::from_secs(300)),
        )
        .unwrap();
    h.registry.workload("bare", Options::new()).unwrap();

    scheduled.processed(10);
    scheduled.item_failed(2);
    std::thread::sleep(Duration::from_millis(50));
    looping.beat();

    let m = h.collect();
    let items: HashMap<_, _> = m["runsignal.workload.items"]
        .iter()
        .map(|p| (p.attributes["runsignal.item.result"].clone(), p.value))
        .collect();
    assert_eq!(
        items,
        HashMap::from([("processed".to_string(), 10.0), ("failed".to_string(), 2.0)])
    );
    assert_eq!(m["runsignal.workload.heartbeats"].len(), 1);

    // A scheduled workload reports its age before its first run; an unscheduled one does not.
    let since = &m["runsignal.workload.since_success"];
    assert_eq!(since.len(), 1);
    assert_eq!(since[0].attributes["runsignal.workload"], "scheduled");
    assert!(since[0].value >= 0.04);
    let beat = &m["runsignal.workload.since_heartbeat"];
    assert_eq!(beat.len(), 1);
    assert!(beat[0].value < 0.04);
}

#[test]
fn registry_validates_names_and_keeps_options_in_one_place() {
    let registry = harness().registry;
    for bad in ["", "Nightly", "has space", "-dash", "order/{id}"] {
        assert!(registry.workload(bad, Options::new()).is_err(), "{bad}");
    }
    assert!(registry
        .workload("rate", Options::new().failure_rate_threshold(1.5))
        .is_err());

    registry
        .workload("sync", Options::new().severity(Severity::Critical))
        .unwrap();
    assert_eq!(
        registry
            .workload("sync", Options::new())
            .unwrap()
            .options()
            .get_severity(),
        Severity::Critical
    );
    assert!(registry
        .workload("sync", Options::new().severity(Severity::Info))
        .is_err());
    assert_eq!(
        registry
            .get_or_add("sync", Options::new().severity(Severity::Info))
            .unwrap()
            .options()
            .get_severity(),
        Severity::Critical
    );
}

#[test]
fn names_match_the_cross_language_vectors() {
    let text = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../spec/conformance/workload-names.json"
    ))
    .unwrap();
    let vectors: serde_json::Value = serde_json::from_str(&text).unwrap();
    for v in vectors["names"].as_array().unwrap() {
        let input = v["input"].as_str().unwrap();
        assert_eq!(
            to_workload_name(input).unwrap(),
            v["expected"].as_str().unwrap(),
            "{input}"
        );
    }
    for input in vectors["invalid"].as_array().unwrap() {
        assert!(to_workload_name(input.as_str().unwrap()).is_err());
    }
}

#[test]
fn manifest_is_byte_for_byte_the_same_as_the_other_sdks() {
    let registry = harness().registry;
    registry
        .workload(
            "stock-sync",
            Options::new()
                .expect_every(Duration::from_secs(300))
                .failure_rate_threshold(0.2),
        )
        .unwrap();
    registry
        .workload(
            "nightly-invoice",
            Options::new()
                .schedule("0 2 * * *")
                .max_duration(Duration::from_secs(1800))
                .min_processed(100)
                .severity(Severity::Critical),
        )
        .unwrap();

    let dir = std::env::temp_dir().join(format!("runsignal-{}", std::process::id()));
    let path = dir.join("out").join("manifest.json");
    registry.write_manifest(&path, "billing").unwrap();

    assert_eq!(
        std::fs::read_to_string(&path).unwrap(),
        r#"{
  "version": 1,
  "service": "billing",
  "workloads": [
    {
      "name": "nightly-invoice",
      "schedule": "0 2 * * *",
      "maxDurationSeconds": 1800,
      "minProcessed": 100,
      "severity": "critical"
    },
    {
      "name": "stock-sync",
      "expectEverySeconds": 300,
      "failureRateThreshold": 0.2,
      "severity": "warning"
    }
  ]
}"#
    );
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn global_registry_reads_the_meter_provider_on_first_use() {
    // Created before the provider is set, as a static or an early call would be.
    let registry = Registry::global();
    let workload = registry.workload("late-provider", Options::new()).unwrap();

    let exporter = InMemoryMetricExporterBuilder::new()
        .with_temporality(Temporality::Delta)
        .build();
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(exporter.clone()).build())
        .build();
    global::set_meter_provider(provider.clone());

    workload.processed(1);
    provider.force_flush().unwrap();
    assert!(!exporter.get_finished_metrics().unwrap().is_empty());
}
