use std::time::Duration;

use crate::Error;

/// How urgent a workload's alerts are; it routes notifications.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Severity {
    /// Worth knowing about; no action needed now.
    Info,
    /// Needs attention during working hours. The default.
    #[default]
    Warning,
    /// Needs attention now, for example by paging on-call.
    Critical,
}

impl Severity {
    /// The severity as the manifest spells it: `info`, `warning` or `critical`.
    pub fn as_str(self) -> &'static str {
        match self {
            Severity::Info => "info",
            Severity::Warning => "warning",
            Severity::Critical => "critical",
        }
    }
}

/// What a workload is expected to do. These define the alerts RunSignal provisions and do not change how signals
/// are recorded.
///
/// ```
/// use std::time::Duration;
/// use runsignal::{Options, Severity};
///
/// let options = Options::new()
///     .schedule("0 2 * * *")
///     .max_duration(Duration::from_secs(30 * 60))
///     .min_processed(100)
///     .severity(Severity::Critical);
/// ```
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Options {
    pub(crate) schedule: Option<String>,
    pub(crate) max_duration: Option<Duration>,
    pub(crate) expect_every: Option<Duration>,
    pub(crate) min_processed: Option<u64>,
    pub(crate) failure_rate_threshold: Option<f64>,
    pub(crate) severity: Severity,
}

impl Options {
    /// No expectations.
    pub fn new() -> Self {
        Self::default()
    }

    /// Cron expression for when the workload should run. Produces the missed alert.
    pub fn schedule(mut self, cron: impl Into<String>) -> Self {
        self.schedule = Some(cron.into());
        self
    }

    /// Longest acceptable run. Produces the overrun alert.
    pub fn max_duration(mut self, d: Duration) -> Self {
        self.max_duration = Some(d);
        self
    }

    /// Longest acceptable gap between heartbeats. Produces the stalled alert.
    pub fn expect_every(mut self, d: Duration) -> Self {
        self.expect_every = Some(d);
        self
    }

    /// Fewest items a run should process. Produces the low-output alert.
    pub fn min_processed(mut self, n: u64) -> Self {
        self.min_processed = Some(n);
        self
    }

    /// Highest acceptable share (0 to 1) of failed runs or items. Produces the failure-rate alert.
    pub fn failure_rate_threshold(mut self, rate: f64) -> Self {
        self.failure_rate_threshold = Some(rate);
        self
    }

    /// How urgent the workload's alerts are.
    pub fn severity(mut self, severity: Severity) -> Self {
        self.severity = severity;
        self
    }

    pub fn get_schedule(&self) -> Option<&str> {
        self.schedule.as_deref()
    }

    pub fn get_max_duration(&self) -> Option<Duration> {
        self.max_duration
    }

    pub fn get_expect_every(&self) -> Option<Duration> {
        self.expect_every
    }

    pub fn get_min_processed(&self) -> Option<u64> {
        self.min_processed
    }

    pub fn get_failure_rate_threshold(&self) -> Option<f64> {
        self.failure_rate_threshold
    }

    pub fn get_severity(&self) -> Severity {
        self.severity
    }

    pub(crate) fn validate(&self) -> Result<(), Error> {
        if let Some(rate) = self.failure_rate_threshold {
            if !(0.0..=1.0).contains(&rate) {
                return Err(Error::InvalidOptions(format!(
                    "failure_rate_threshold must be between 0 and 1, got {rate}"
                )));
            }
        }
        if self.max_duration.is_some_and(|d| d.is_zero())
            || self.expect_every.is_some_and(|d| d.is_zero())
        {
            return Err(Error::InvalidOptions("durations must be positive".into()));
        }
        Ok(())
    }
}
