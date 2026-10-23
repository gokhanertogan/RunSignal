//! Two workloads that HTTP monitoring never sees: a ledger batch that runs every minute and a stock-sync loop.
//! With NEW_RELIC_LICENSE_KEY set, signals go to New Relic over OTLP (NEW_RELIC_REGION=EU for the EU data
//! center); otherwise nothing is exported.
//!
//!     RUNSIGNAL_MANIFEST_PATH=runsignal.json cargo run --example worker

use std::collections::HashMap;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use opentelemetry::{global, KeyValue};
use opentelemetry_otlp::{WithExportConfig, WithHttpConfig};
use opentelemetry_sdk::metrics::{PeriodicReader, SdkMeterProvider, Temporality};
use opentelemetry_sdk::Resource;
use runsignal::{Options, Registry, Severity};

const SERVICE: &str = "runsignal-rust-sample";

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let provider = meter_provider()?;
    global::set_meter_provider(provider.clone());

    let registry = Registry::global();
    let ledger = registry.workload(
        "ledger-batch",
        Options::new()
            .schedule("0 * * * * *")
            .max_duration(Duration::from_secs(30))
            .severity(Severity::Critical),
    )?;
    let stock = registry.workload(
        "stock-sync",
        Options::new()
            .expect_every(Duration::from_secs(300))
            .failure_rate_threshold(0.2),
    )?;
    if let Some(path) = registry.write_manifest_from_env(Some(SERVICE))? {
        println!("wrote the RunSignal manifest to {}", path.display());
    }

    // A batch at the start of every minute.
    std::thread::spawn(move || loop {
        let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap();
        std::thread::sleep(Duration::from_secs(60 - now.as_secs() % 60));
        let _ = ledger.run(|| {
            std::thread::sleep(Duration::from_millis(100 + random(700)));
            if random(100) < 20 {
                return Err(std::io::Error::other("ledger API did not answer"));
            }
            ledger.processed(1 + random(20));
            Ok(())
        });
    });

    // A long-running loop with a heartbeat on every pass.
    loop {
        stock.processed(random(50));
        if random(100) < 10 {
            stock.item_failed(1);
        }
        stock.beat();
        std::thread::sleep(Duration::from_secs(2));
    }
}

fn meter_provider() -> Result<SdkMeterProvider, Box<dyn std::error::Error>> {
    let resource = Resource::builder()
        .with_attribute(KeyValue::new("service.name", SERVICE))
        .build();
    let Ok(license_key) = std::env::var("NEW_RELIC_LICENSE_KEY") else {
        let exporter = in_memory_reader();
        return Ok(SdkMeterProvider::builder()
            .with_resource(resource)
            .with_reader(exporter)
            .build());
    };

    let endpoint = if std::env::var("NEW_RELIC_REGION").is_ok_and(|r| r.eq_ignore_ascii_case("EU"))
    {
        "https://otlp.eu01.nr-data.net:4318/v1/metrics"
    } else {
        "https://otlp.nr-data.net:4318/v1/metrics"
    };
    // Delta temporality is required: with cumulative, a stopped workload never looks stopped.
    let exporter = opentelemetry_otlp::MetricExporter::builder()
        .with_http()
        .with_endpoint(endpoint)
        .with_headers(HashMap::from([("api-key".to_string(), license_key)]))
        .with_temporality(Temporality::Delta)
        .build()?;
    let reader = PeriodicReader::builder(exporter)
        .with_interval(Duration::from_secs(30))
        .build();
    Ok(SdkMeterProvider::builder()
        .with_resource(resource)
        .with_reader(reader)
        .build())
}

// Without New Relic the metrics are kept in memory and dropped: this example is about the New Relic path.
fn in_memory_reader() -> PeriodicReader<opentelemetry_sdk::metrics::InMemoryMetricExporter> {
    PeriodicReader::builder(opentelemetry_sdk::metrics::InMemoryMetricExporter::default()).build()
}

fn random(below: u64) -> u64 {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .subsec_nanos() as u64;
    (nanos / 7 + nanos % 1013) % below
}
