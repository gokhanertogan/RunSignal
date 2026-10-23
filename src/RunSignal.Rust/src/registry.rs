use std::collections::BTreeMap;
use std::future::Future;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, OnceLock, RwLock, Weak};
use std::time::{Duration, Instant};

use opentelemetry::context::FutureExt;
use opentelemetry::metrics::{Counter, Histogram, Meter, ObservableGauge};
use opentelemetry::trace::{SpanKind, Status, TraceContextExt, Tracer};
use opentelemetry::{global, Context, InstrumentationScope, KeyValue};

use crate::naming::validate_name;
use crate::{Error, Options, DURATION_BUCKETS, SCOPE_NAME, VERSION};

struct Instruments {
    runs: Counter<u64>,
    duration: Histogram<f64>,
    items: Counter<u64>,
    heartbeats: Counter<u64>,
    // Kept alive so their callbacks keep running.
    _since_success: ObservableGauge<f64>,
    _since_heartbeat: ObservableGauge<f64>,
}

struct Inner {
    workloads: RwLock<BTreeMap<String, Arc<WorkloadInner>>>,
    base: Instant,
    meter: Option<Meter>,
    tracing: bool,
    instruments: OnceLock<Instruments>,
}

/// Creates and holds the workloads of an application and the instruments they record on. Cheap to clone.
#[derive(Clone)]
pub struct Registry {
    inner: Arc<Inner>,
}

impl Registry {
    /// A registry on the global OpenTelemetry providers, with a span per run. The global meter is read on first
    /// use, not here: a meter taken before `global::set_meter_provider` would stay a no-op for good.
    pub fn global() -> Self {
        Self::build(None, true)
    }

    /// A registry that records on `meter`, with a span per run on the global tracer provider.
    pub fn with_meter(meter: Meter) -> Self {
        Self::build(Some(meter), true)
    }

    /// Records no span per run, for workloads that run thousands of times per second. Metrics are unaffected.
    pub fn without_tracing(self) -> Self {
        let inner = Arc::try_unwrap(self.inner)
            .unwrap_or_else(|_| panic!("call without_tracing before cloning the registry"));
        Self::build(inner.meter, false)
    }

    fn build(meter: Option<Meter>, tracing: bool) -> Self {
        Registry {
            inner: Arc::new(Inner {
                workloads: RwLock::new(BTreeMap::new()),
                base: Instant::now(),
                meter,
                tracing,
                instruments: OnceLock::new(),
            }),
        }
    }

    /// Returns the workload called `name`, creating it with `options` on first use. Keep the result. Passing
    /// options other than [`Options::new()`] for a workload that already exists is an error, so expectations
    /// are declared in exactly one place.
    pub fn workload(&self, name: &str, options: Options) -> Result<Workload, Error> {
        let (workload, created) = self.get_or_add_inner(name, &options)?;
        if !created && options != Options::new() {
            return Err(Error::InvalidOptions(format!(
                "workload '{name}' is already registered; set its options only where it is first created"
            )));
        }
        Ok(workload)
    }

    /// Returns the workload called `name`, creating it with `options` if it does not exist yet. Unlike
    /// [`Registry::workload`], an existing workload is returned as is: options declared elsewhere take
    /// precedence. Scheduler integrations use it to name workloads after jobs.
    pub fn get_or_add(&self, name: &str, options: Options) -> Result<Workload, Error> {
        Ok(self.get_or_add_inner(name, &options)?.0)
    }

    fn get_or_add_inner(&self, name: &str, options: &Options) -> Result<(Workload, bool), Error> {
        if let Some(existing) = self.inner.workloads.read().expect("poisoned").get(name) {
            return Ok((
                Workload {
                    registry: self.clone(),
                    inner: existing.clone(),
                },
                false,
            ));
        }
        validate_name(name)?;
        options.validate()?;
        let mut workloads = self.inner.workloads.write().expect("poisoned");
        if let Some(existing) = workloads.get(name) {
            return Ok((
                Workload {
                    registry: self.clone(),
                    inner: existing.clone(),
                },
                false,
            ));
        }
        let created = Arc::new(WorkloadInner::new(name, options.clone(), self.now()));
        workloads.insert(name.to_string(), created.clone());
        Ok((
            Workload {
                registry: self.clone(),
                inner: created,
            },
            true,
        ))
    }

    /// Every workload created so far, sorted by name.
    pub fn workloads(&self) -> Vec<Workload> {
        self.inner
            .workloads
            .read()
            .expect("poisoned")
            .values()
            .map(|w| Workload {
                registry: self.clone(),
                inner: w.clone(),
            })
            .collect()
    }

    fn now(&self) -> u64 {
        self.inner.base.elapsed().as_nanos() as u64
    }

    fn instruments(&self) -> &Instruments {
        self.inner.instruments.get_or_init(|| {
            let meter = self.inner.meter.clone().unwrap_or_else(|| {
                global::meter_with_scope(InstrumentationScope::builder(SCOPE_NAME).with_version(VERSION).build())
            });
            let weak = Arc::downgrade(&self.inner);
            Instruments {
                runs: meter.u64_counter(crate::METRIC_RUNS).with_unit("{run}").with_description("Completed runs of a workload.").build(),
                duration: meter
                    .f64_histogram(crate::METRIC_DURATION)
                    .with_unit("s")
                    .with_description("Duration of workload runs.")
                    .with_boundaries(DURATION_BUCKETS.to_vec())
                    .build(),
                items: meter.u64_counter(crate::METRIC_ITEMS).with_unit("{item}").with_description("Items handled by workload runs.").build(),
                heartbeats: meter
                    .u64_counter(crate::METRIC_HEARTBEATS)
                    .with_unit("{heartbeat}")
                    .with_description("Heartbeats of long-running workloads.")
                    .build(),
                _since_success: meter
                    .f64_observable_gauge(crate::METRIC_SINCE_SUCCESS)
                    .with_unit("s")
                    .with_description("Seconds since the last successful run of a workload, or since it was registered.")
                    .with_callback(observe(weak.clone(), WorkloadInner::seconds_since_success))
                    .build(),
                _since_heartbeat: meter
                    .f64_observable_gauge(crate::METRIC_SINCE_HEARTBEAT)
                    .with_unit("s")
                    .with_description("Seconds since the last heartbeat of a workload, or since it was registered.")
                    .with_callback(observe(weak, WorkloadInner::seconds_since_heartbeat))
                    .build(),
            }
        })
    }

    /// The workload manifest as JSON, in the format of spec/manifest.schema.json. `service` must match the
    /// OpenTelemetry `service.name` the signals are exported with.
    pub fn manifest_json(&self, service: &str) -> String {
        crate::manifest::write(service, &self.workloads())
    }

    /// Writes the manifest to `path` atomically, so a reader never sees half a manifest.
    pub fn write_manifest(
        &self,
        path: impl AsRef<std::path::Path>,
        service: &str,
    ) -> std::io::Result<()> {
        let path = path.as_ref();
        if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)?;
        }
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, self.manifest_json(service))?;
        std::fs::rename(tmp, path)
    }

    /// Writes the manifest if the `RUNSIGNAL_MANIFEST_PATH` environment variable is set, so CI can ask for one
    /// without code changes. Call it once every workload is registered. The service name is `service`, or else
    /// `OTEL_SERVICE_NAME`. Returns the path written, if any.
    pub fn write_manifest_from_env(
        &self,
        service: Option<&str>,
    ) -> Result<Option<std::path::PathBuf>, Error> {
        let Some(path) = std::env::var_os("RUNSIGNAL_MANIFEST_PATH").filter(|p| !p.is_empty())
        else {
            return Ok(None);
        };
        let service = match service {
            Some(s) if !s.is_empty() => s.to_string(),
            _ => std::env::var("OTEL_SERVICE_NAME").map_err(|_| {
                Error::InvalidOptions(
                    "pass a service name or set OTEL_SERVICE_NAME to write the manifest".into(),
                )
            })?,
        };
        let path = std::path::PathBuf::from(path);
        self.write_manifest(&path, &service).map_err(Error::Io)?;
        Ok(Some(path))
    }
}

fn observe(
    registry: Weak<Inner>,
    seconds: fn(&WorkloadInner, u64) -> Option<f64>,
) -> impl Fn(&dyn opentelemetry::metrics::AsyncInstrument<f64>) + Send + Sync + 'static {
    move |observer| {
        let Some(inner) = registry.upgrade() else {
            return;
        };
        let now = inner.base.elapsed().as_nanos() as u64;
        for w in inner.workloads.read().expect("poisoned").values() {
            if let Some(s) = seconds(w, now) {
                observer.observe(s, &w.name_attributes);
            }
        }
    }
}

struct WorkloadInner {
    name: String,
    options: Options,
    name_attributes: [KeyValue; 1],
    success: [KeyValue; 2],
    failure: [KeyValue; 2],
    cancelled: [KeyValue; 2],
    processed: [KeyValue; 2],
    failed: [KeyValue; 2],
    // Nanoseconds since the registry was created. Until the first success or heartbeat they hold the time the
    // workload was created, so a workload that never succeeds or never beats still reports a growing age.
    last_success: AtomicU64,
    last_heartbeat: AtomicU64,
    has_started: AtomicBool,
    has_beaten: AtomicBool,
}

impl WorkloadInner {
    fn new(name: &str, options: Options, now: u64) -> Self {
        let workload = KeyValue::new(crate::ATTRIBUTE_WORKLOAD, name.to_string());
        let with =
            |key: &'static str, value: &'static str| [workload.clone(), KeyValue::new(key, value)];
        WorkloadInner {
            name: name.to_string(),
            options,
            success: with(crate::ATTRIBUTE_OUTCOME, "success"),
            failure: with(crate::ATTRIBUTE_OUTCOME, "failure"),
            cancelled: with(crate::ATTRIBUTE_OUTCOME, "cancelled"),
            processed: with(crate::ATTRIBUTE_ITEM_RESULT, "processed"),
            failed: with(crate::ATTRIBUTE_ITEM_RESULT, "failed"),
            name_attributes: [workload],
            last_success: AtomicU64::new(now),
            last_heartbeat: AtomicU64::new(now),
            has_started: AtomicBool::new(false),
            has_beaten: AtomicBool::new(false),
        }
    }

    // Reported for workloads that are scheduled or have run, so alerts can tell a missed run from a workload
    // that simply has no runs.
    fn seconds_since_success(&self, now: u64) -> Option<f64> {
        (self.has_started.load(Ordering::Relaxed) || self.options.schedule.is_some())
            .then(|| now.saturating_sub(self.last_success.load(Ordering::Relaxed)) as f64 / 1e9)
    }

    // Reported for workloads that expect heartbeats or have sent one.
    fn seconds_since_heartbeat(&self, now: u64) -> Option<f64> {
        (self.has_beaten.load(Ordering::Relaxed) || self.options.expect_every.is_some())
            .then(|| now.saturating_sub(self.last_heartbeat.load(Ordering::Relaxed)) as f64 / 1e9)
    }
}

/// A unit of background work, such as a scheduled job, a consumer or a sync loop. Get one from a [`Registry`]
/// once and keep it; it is cheap to clone and safe to share between threads.
#[derive(Clone)]
pub struct Workload {
    registry: Registry,
    inner: Arc<WorkloadInner>,
}

impl Workload {
    /// The workload's stable name, such as `nightly-invoice`.
    pub fn name(&self) -> &str {
        &self.inner.name
    }

    /// What the workload is expected to do.
    pub fn options(&self) -> &Options {
        &self.inner.options
    }

    /// Starts a run. End it with [`Run::succeed`], [`Run::fail`], [`Run::fail_code`] or [`Run::cancel`]; each takes
    /// the run by value, so a run cannot be ended twice. A run that is dropped without being ended records nothing,
    /// which the backend sees as a missed run. Prefer [`Workload::run`] or [`Workload::run_async`].
    pub fn start(&self) -> Run {
        if !self.inner.has_started.load(Ordering::Relaxed) {
            self.inner.has_started.store(true, Ordering::Relaxed);
        }
        let context = self.registry.inner.tracing.then(|| {
            let tracer = global::tracer_with_scope(
                InstrumentationScope::builder(SCOPE_NAME)
                    .with_version(VERSION)
                    .build(),
            );
            let span = tracer
                .span_builder(self.inner.name.clone())
                .with_kind(SpanKind::Internal)
                .with_attributes(self.inner.name_attributes.to_vec())
                .start(&tracer);
            Context::current_with_span(span)
        });
        Run {
            workload: self.clone(),
            start: self.registry.now(),
            context,
        }
    }

    /// Runs `work` as one run: it succeeds when `work` returns `Ok` and fails with the error's type name when it
    /// returns `Err`. The result is passed through. The run's span is current while `work` runs.
    pub fn run<T, E>(&self, work: impl FnOnce() -> Result<T, E>) -> Result<T, E> {
        let run = self.start();
        let result = {
            let _guard = run.context.clone().map(Context::attach);
            work()
        };
        match &result {
            Ok(_) => run.succeed(),
            Err(e) => run.fail(e),
        }
        result
    }

    /// The async form of [`Workload::run`]. A future that is dropped before it completes, for example on
    /// shutdown or by a timeout, records a cancelled run.
    pub async fn run_async<T, E, F>(&self, work: F) -> Result<T, E>
    where
        F: Future<Output = Result<T, E>>,
    {
        let mut guard = CancelOnDrop(Some(self.start()));
        let result = match guard.0.as_ref().and_then(|r| r.context.clone()) {
            Some(cx) => work.with_context(cx).await,
            None => work.await,
        };
        let run = guard.0.take().expect("run is taken once");
        match &result {
            Ok(_) => run.succeed(),
            Err(e) => run.fail(e),
        }
        result
    }

    /// Records `count` successfully processed items. Recording 0 is useful: it reports that a run did nothing.
    pub fn processed(&self, count: u64) {
        self.registry
            .instruments()
            .items
            .add(count, &self.inner.processed);
    }

    /// Records `count` items that could not be processed.
    pub fn item_failed(&self, count: u64) {
        self.registry
            .instruments()
            .items
            .add(count, &self.inner.failed);
    }

    /// Records a heartbeat: the workload is alive and making progress.
    pub fn beat(&self) {
        self.inner
            .last_heartbeat
            .store(self.registry.now(), Ordering::Relaxed);
        if !self.inner.has_beaten.load(Ordering::Relaxed) {
            self.inner.has_beaten.store(true, Ordering::Relaxed);
        }
        self.registry
            .instruments()
            .heartbeats
            .add(1, &self.inner.name_attributes);
    }

    fn complete(
        &self,
        start: u64,
        context: Option<Context>,
        outcome: &'static str,
        error_type: Option<String>,
        description: Option<String>,
    ) {
        let instruments = self.registry.instruments();
        let now = self.registry.now();
        let attributes = match outcome {
            "success" => &self.inner.success,
            "failure" => &self.inner.failure,
            _ => &self.inner.cancelled,
        };
        match &error_type {
            None => instruments.runs.add(1, attributes),
            Some(t) => {
                let mut with_error = attributes.to_vec();
                with_error.push(KeyValue::new(crate::ATTRIBUTE_ERROR_TYPE, t.clone()));
                instruments.runs.add(1, &with_error);
            }
        }
        instruments.duration.record(
            Duration::from_nanos(now.saturating_sub(start)).as_secs_f64(),
            attributes,
        );
        if outcome == "success" {
            self.inner.last_success.store(now, Ordering::Relaxed);
        }

        if let Some(cx) = context {
            let span = cx.span();
            span.set_attribute(KeyValue::new(crate::ATTRIBUTE_OUTCOME, outcome));
            match outcome {
                "success" => span.set_status(Status::Ok),
                "failure" => {
                    span.set_attribute(KeyValue::new(
                        crate::ATTRIBUTE_ERROR_TYPE,
                        error_type.unwrap_or_default(),
                    ));
                    span.set_status(Status::error(description.unwrap_or_default()));
                }
                _ => {}
            }
            span.end();
        }
    }
}

/// One run of a [`Workload`], started with [`Workload::start`]. Each way of ending it takes it by value, so it
/// is recorded exactly once.
#[must_use = "end the run with succeed, fail, fail_code or cancel"]
pub struct Run {
    workload: Workload,
    start: u64,
    context: Option<Context>,
}

impl Run {
    /// Time since the run started.
    pub fn elapsed(&self) -> Duration {
        Duration::from_nanos(self.workload.registry.now().saturating_sub(self.start))
    }

    /// The OpenTelemetry context carrying the run's span, to attach to work done for the run. `None` when
    /// tracing is off.
    pub fn context(&self) -> Option<&Context> {
        self.context.as_ref()
    }

    /// Ends the run as successful.
    pub fn succeed(self) {
        self.workload
            .complete(self.start, self.context, "success", None, None);
    }

    /// Ends the run as failed because of `error`. Its `error.type` is the compiler's name for the error type,
    /// from [`std::any::type_name`], such as `core::io::error::Error`. That name can change between Rust
    /// versions, so use [`Run::fail_code`] when dashboards or alerts filter on specific errors.
    pub fn fail<E>(self, error: &E) {
        self.workload.complete(
            self.start,
            self.context,
            "failure",
            Some(std::any::type_name::<E>().to_string()),
            None,
        );
        let _ = error;
    }

    /// Ends the run as failed without an error value, for example when an expected input file is missing.
    /// `code` must be a short, stable value such as `statement_missing`; never put messages or IDs in it.
    pub fn fail_code(self, code: &str, description: &str) {
        let code = if code.is_empty() { "unknown" } else { code };
        self.workload.complete(
            self.start,
            self.context,
            "failure",
            Some(code.to_string()),
            Some(description.to_string()),
        );
    }

    /// Ends the run as cancelled, for example on shutdown. Cancelled runs do not count as failures.
    pub fn cancel(self) {
        self.workload
            .complete(self.start, self.context, "cancelled", None, None);
    }
}

// Records a cancelled run when a run_async future is dropped before it completes.
struct CancelOnDrop(Option<Run>);

impl Drop for CancelOnDrop {
    fn drop(&mut self) {
        if let Some(run) = self.0.take() {
            run.cancel();
        }
    }
}
