//! RunSignal records runs, items and heartbeats of background work (scheduled jobs, consumers, sync loops,
//! data pipelines) as OpenTelemetry metrics and spans, so you can alert on work that no HTTP status code
//! describes: missed, overrunning, stalled or low-output jobs.
//!
//! The metric names, attributes and manifest format are the same in every RunSignal SDK (see spec/README.md in
//! the repository), so one set of alert rules covers Rust, .NET, Go, Java and Node.js services.
//!
//! ```
//! use std::time::Duration;
//! use runsignal::{Options, Registry, Severity};
//!
//! let registry = Registry::global();
//! let invoices = registry.workload("nightly-invoice", Options::new()
//!     .schedule("0 2 * * *")
//!     .max_duration(Duration::from_secs(30 * 60))
//!     .severity(Severity::Critical))?;
//!
//! invoices.run(|| -> Result<(), std::io::Error> {
//!     invoices.processed(10);
//!     Ok(())
//! })?;
//! # Ok::<(), Box<dyn std::error::Error>>(())
//! ```

mod manifest;
mod naming;
mod options;
mod registry;

pub use naming::{to_workload_name, validate_name};
pub use options::{Options, Severity};
pub use registry::{Registry, Run, Workload};

/// Name of the OpenTelemetry meter and tracer.
pub const SCOPE_NAME: &str = "RunSignal";
/// The SDK version, reported as the instrumentation scope version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

pub const METRIC_RUNS: &str = "runsignal.workload.runs";
pub const METRIC_DURATION: &str = "runsignal.workload.duration";
pub const METRIC_ITEMS: &str = "runsignal.workload.items";
pub const METRIC_HEARTBEATS: &str = "runsignal.workload.heartbeats";
pub const METRIC_SINCE_SUCCESS: &str = "runsignal.workload.since_success";
pub const METRIC_SINCE_HEARTBEAT: &str = "runsignal.workload.since_heartbeat";

pub const ATTRIBUTE_WORKLOAD: &str = "runsignal.workload";
pub const ATTRIBUTE_OUTCOME: &str = "runsignal.outcome";
pub const ATTRIBUTE_ITEM_RESULT: &str = "runsignal.item.result";
pub const ATTRIBUTE_ERROR_TYPE: &str = "error.type";

/// Histogram bucket boundaries, in seconds, for the duration metric: background work runs from milliseconds to
/// hours, while the OpenTelemetry defaults stop at 10 seconds.
pub const DURATION_BUCKETS: [f64; 18] = [
    0.01, 0.05, 0.1, 0.5, 1.0, 5.0, 10.0, 30.0, 60.0, 120.0, 300.0, 600.0, 900.0, 1800.0, 3600.0,
    7200.0, 14400.0, 28800.0,
];

/// Errors from RunSignal.
#[derive(Debug)]
pub enum Error {
    /// A workload name is not 1-128 lowercase letters, digits, '.', '_' or '-'.
    InvalidName(String),
    /// Options are out of range or declared twice.
    InvalidOptions(String),
    /// The manifest could not be written.
    Io(std::io::Error),
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Error::InvalidName(m) | Error::InvalidOptions(m) => write!(f, "runsignal: {m}"),
            Error::Io(e) => write!(f, "runsignal: writing the manifest: {e}"),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Io(e) => Some(e),
            _ => None,
        }
    }
}
