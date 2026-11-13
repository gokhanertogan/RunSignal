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
    pub(crate) owner: Option<String>,
    pub(crate) runbook_url: Option<String>,
}

const MAX_OWNER_LENGTH: usize = 128;

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

    /// Who answers the workload's alerts: a team or person, such as `billing-team`. Added to alert descriptions,
    /// and as the `owner` label on Prometheus rules. Must not be blank; at most 128 characters.
    pub fn owner(mut self, owner: impl Into<String>) -> Self {
        self.owner = Some(owner.into());
        self
    }

    /// Absolute `http` or `https` URL of what to do when an alert fires. Added to alert descriptions, and as the
    /// `runbook_url` annotation on Prometheus rules.
    pub fn runbook_url(mut self, url: impl Into<String>) -> Self {
        self.runbook_url = Some(url.into());
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

    pub fn get_owner(&self) -> Option<&str> {
        self.owner.as_deref()
    }

    pub fn get_runbook_url(&self) -> Option<&str> {
        self.runbook_url.as_deref()
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
        if let Some(owner) = &self.owner {
            if owner.trim().is_empty() {
                return Err(Error::InvalidOptions("owner must not be blank".into()));
            }
            let length = owner.chars().count();
            if length > MAX_OWNER_LENGTH {
                return Err(Error::InvalidOptions(format!(
                    "owner must be at most {MAX_OWNER_LENGTH} characters, got {length}"
                )));
            }
        }
        if let Some(url) = &self.runbook_url {
            if !is_http_url(url) {
                return Err(Error::InvalidOptions(format!(
                    "runbook_url must be an absolute http or https URL, got {url:?}"
                )));
            }
        }
        Ok(())
    }
}

/// An absolute `http://` or `https://` URL with a host and no whitespace or control characters.
fn is_http_url(url: &str) -> bool {
    let Some(rest) = url
        .strip_prefix("https://")
        .or_else(|| url.strip_prefix("http://"))
    else {
        return false;
    };
    let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
    let host = authority.rsplit('@').next().unwrap_or("");
    !host.is_empty()
        && !host.starts_with(':')
        && !url.chars().any(|c| c.is_whitespace() || c.is_control())
}
