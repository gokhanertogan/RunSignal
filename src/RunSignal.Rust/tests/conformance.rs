//! spec/conformance/signals.json: every SDK runs the same scenario and must emit exactly the same metrics.

use std::collections::BTreeMap;
use std::time::Duration;

use opentelemetry::metrics::MeterProvider;
use opentelemetry_sdk::metrics::data::{AggregatedMetrics, MetricData};
use opentelemetry_sdk::metrics::{InMemoryMetricExporter, PeriodicReader, SdkMeterProvider};
use runsignal::{Options, Registry, Severity};
use serde_json::{json, Value};

#[test]
fn scenario_produces_exactly_the_expected_metrics() {
    let text = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../spec/conformance/signals.json"
    ))
    .unwrap();
    let spec: Value = serde_json::from_str(&text).unwrap();

    let exporter = InMemoryMetricExporter::default();
    let provider = SdkMeterProvider::builder()
        .with_reader(PeriodicReader::builder(exporter.clone()).build())
        .build();
    let registry = Registry::with_meter(provider.meter("RunSignal")).without_tracing();

    for w in spec["workloads"].as_array().unwrap() {
        let mut o = Options::new();
        if let Some(s) = w["schedule"].as_str() {
            o = o.schedule(s);
        }
        if let Some(s) = w["maxDurationSeconds"].as_f64() {
            o = o.max_duration(Duration::from_secs_f64(s));
        }
        if let Some(s) = w["expectEverySeconds"].as_f64() {
            o = o.expect_every(Duration::from_secs_f64(s));
        }
        if let Some(n) = w["minProcessed"].as_u64() {
            o = o.min_processed(n);
        }
        if let Some(r) = w["failureRateThreshold"].as_f64() {
            o = o.failure_rate_threshold(r);
        }
        if let Some(s) = w["severity"].as_str() {
            o = o.severity(match s {
                "info" => Severity::Info,
                "critical" => Severity::Critical,
                _ => Severity::Warning,
            });
        }
        registry.workload(w["name"].as_str().unwrap(), o).unwrap();
    }

    for a in spec["actions"].as_array().unwrap() {
        let w = registry
            .workload(a["workload"].as_str().unwrap(), Options::new())
            .unwrap();
        let count = || a["count"].as_u64().unwrap();
        match a["action"].as_str().unwrap() {
            "succeed" => w.start().succeed(),
            "failCode" => w.start().fail_code(a["code"].as_str().unwrap(), ""),
            "cancel" => w.start().cancel(),
            "processed" => w.processed(count()),
            "itemFailed" => w.item_failed(count()),
            "beat" => w.beat(),
            other => panic!("unknown action {other}"),
        }
    }

    provider.force_flush().unwrap();
    let mut got = Vec::new();
    for rm in exporter.get_finished_metrics().unwrap() {
        for sm in rm.scope_metrics() {
            for metric in sm.metrics() {
                let attrs = |kvs: &mut dyn Iterator<Item = &opentelemetry::KeyValue>| -> Value {
                    kvs.map(|kv| (kv.key.to_string(), Value::String(kv.value.to_string())))
                        .collect::<serde_json::Map<_, _>>()
                        .into()
                };
                let mut m = json!({ "name": metric.name(), "unit": metric.unit() });
                let mut points = Vec::new();
                match metric.data() {
                    AggregatedMetrics::U64(MetricData::Sum(sum)) => {
                        m["type"] = json!("counter");
                        for dp in sum.data_points() {
                            points.push(json!({ "attributes": attrs(&mut dp.attributes()), "value": dp.value() as f64 }));
                        }
                    }
                    AggregatedMetrics::F64(MetricData::Histogram(h)) => {
                        m["type"] = json!("histogram");
                        for dp in h.data_points() {
                            m["bounds"] = json!(dp.bounds().collect::<Vec<_>>());
                            points.push(json!({ "attributes": attrs(&mut dp.attributes()), "count": dp.count() as f64 }));
                        }
                    }
                    AggregatedMetrics::F64(MetricData::Gauge(g)) => {
                        m["type"] = json!("gauge");
                        for dp in g.data_points() {
                            points.push(json!({ "attributes": attrs(&mut dp.attributes()) }));
                        }
                    }
                    other => m["type"] = json!(format!("{other:?}")),
                }
                if !points.is_empty() {
                    m["points"] = Value::Array(points);
                    got.push(m);
                }
            }
        }
    }

    assert_eq!(canonical(&Value::Array(got)), canonical(&spec["expected"]));
}

// Order-insensitive form: metrics sorted by name, points by their attributes, numbers as floats.
fn canonical(metrics: &Value) -> String {
    let mut sorted: Vec<BTreeMap<String, Value>> = metrics
        .as_array()
        .unwrap()
        .iter()
        .map(|m| {
            let mut metric: BTreeMap<String, Value> =
                m.as_object().unwrap().clone().into_iter().collect();
            if let Some(bounds) = metric.get_mut("bounds") {
                *bounds = json!(bounds
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|b| b.as_f64().unwrap())
                    .collect::<Vec<_>>());
            }
            let mut points: Vec<Value> = metric["points"]
                .as_array()
                .unwrap()
                .iter()
                .map(|p| {
                    let mut point = serde_json::Map::new();
                    point.insert("attributes".into(), p["attributes"].clone());
                    for key in ["value", "count"] {
                        if let Some(n) = p.get(key).and_then(Value::as_f64) {
                            point.insert(key.into(), json!(n));
                        }
                    }
                    Value::Object(point)
                })
                .collect();
            points.sort_by_key(|p| p["attributes"].to_string());
            metric.insert("points".into(), Value::Array(points));
            metric
        })
        .collect();
    sorted.sort_by(|a, b| a["name"].as_str().cmp(&b["name"].as_str()));
    serde_json::to_string_pretty(&sorted).unwrap()
}
