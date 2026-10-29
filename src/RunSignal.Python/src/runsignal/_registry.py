from __future__ import annotations

import functools
import inspect
import json
import os
import re
import sys
import threading
import time
from datetime import timedelta
from typing import Any, Callable, Iterable, Literal, TypeVar, Union

from opentelemetry import context, metrics, trace
from opentelemetry.metrics import CallbackOptions, MeterProvider, Observation
from opentelemetry.trace import Span, SpanKind, Status, StatusCode, TracerProvider

from ._naming import to_workload_name, validate_name
from ._telemetry import DURATION_BUCKETS, SCOPE_NAME, VERSION, Attributes, Metrics

Severity = Literal["info", "warning", "critical"]
"""How urgent a workload's alerts are; it routes notifications."""

Duration = Union[float, int, timedelta, str]
"""Seconds, a ``timedelta``, or a string such as ``'90s'``, ``'30m'``, ``'2h'`` or ``'1d'``."""

F = TypeVar("F", bound=Callable[..., Any])

_SEVERITIES = ("info", "warning", "critical")
_UNIT_SECONDS = {"s": 1, "m": 60, "h": 3600, "d": 86400}


class WorkloadConfigurationError(ValueError):
    """Raised when a workload's options are invalid or declared twice."""


def _to_seconds(value: Duration, option: str) -> float:
    if isinstance(value, timedelta):
        seconds = value.total_seconds()
    elif isinstance(value, str):
        match = re.fullmatch(r"(\d+(?:\.\d+)?)([smhd])", value.strip())
        if not match:
            raise WorkloadConfigurationError(
                f"{option} '{value}' is not a duration. Use seconds, a timedelta or a number with s, m, h or d, such as '30m'."
            )
        seconds = float(match.group(1)) * _UNIT_SECONDS[match.group(2)]
    elif isinstance(value, (int, float)) and not isinstance(value, bool):
        seconds = float(value)
    else:
        raise WorkloadConfigurationError(f"{option} must be seconds, a timedelta or a string such as '30m'.")
    if not seconds > 0 or seconds == float("inf"):
        raise WorkloadConfigurationError(f"{option} must be positive.")
    return seconds


class Expectations:
    """The validated expectations of a workload, with durations in seconds. They define the alerts RunSignal
    provisions and do not change how signals are recorded."""

    __slots__ = ("schedule", "max_duration", "expect_every", "min_processed", "failure_rate_threshold", "severity")

    def __init__(
        self,
        schedule: str | None = None,
        max_duration: Duration | None = None,
        expect_every: Duration | None = None,
        min_processed: int | None = None,
        failure_rate_threshold: float | None = None,
        severity: Severity | None = None,
    ) -> None:
        if failure_rate_threshold is not None and not 0 <= failure_rate_threshold <= 1:
            raise WorkloadConfigurationError(f"failure_rate_threshold must be between 0 and 1, got {failure_rate_threshold}.")
        if min_processed is not None and (not isinstance(min_processed, int) or isinstance(min_processed, bool) or min_processed < 0):
            raise WorkloadConfigurationError("min_processed must be a non-negative integer.")
        severity = severity or "warning"
        if severity not in _SEVERITIES:
            raise WorkloadConfigurationError(f"Unknown severity '{severity}'.")
        self.schedule = schedule
        self.max_duration = None if max_duration is None else _to_seconds(max_duration, "max_duration")
        self.expect_every = None if expect_every is None else _to_seconds(expect_every, "expect_every")
        self.min_processed = min_processed
        self.failure_rate_threshold = failure_rate_threshold
        self.severity: Severity = severity


class _Instruments:
    def __init__(self, registry: WorkloadRegistry, provider: MeterProvider) -> None:
        meter = provider.get_meter(SCOPE_NAME, VERSION)
        self.runs = meter.create_counter(Metrics.RUNS, unit="{run}", description="Completed runs of a workload.")
        self.duration = meter.create_histogram(
            Metrics.DURATION,
            unit="s",
            description="Duration of workload runs.",
            explicit_bucket_boundaries_advisory=list(DURATION_BUCKETS),
        )
        self.items = meter.create_counter(Metrics.ITEMS, unit="{item}", description="Items handled by workload runs.")
        self.heartbeats = meter.create_counter(
            Metrics.HEARTBEATS, unit="{heartbeat}", description="Heartbeats of long-running workloads."
        )
        meter.create_observable_gauge(
            Metrics.SINCE_SUCCESS,
            callbacks=[registry._observe_since_success],
            unit="s",
            description="Seconds since the last successful run of a workload, or since it was registered.",
        )
        meter.create_observable_gauge(
            Metrics.SINCE_HEARTBEAT,
            callbacks=[registry._observe_since_heartbeat],
            unit="s",
            description="Seconds since the last heartbeat of a workload, or since it was registered.",
        )


class WorkloadRegistry:
    """Creates and holds the workloads of an application and the instruments they record on.

    By default it records on the global OpenTelemetry providers. They forward to the SDK once it is configured,
    so a registry created by an import before the SDK starts still records.
    """

    def __init__(
        self,
        *,
        meter_provider: MeterProvider | None = None,
        tracer_provider: TracerProvider | None = None,
        tracing: bool = True,
    ) -> None:
        self._workloads: dict[str, Workload] = {}
        self._lock = threading.Lock()
        self._instruments = _Instruments(self, meter_provider or metrics.get_meter_provider())
        self._tracer = (tracer_provider or trace.get_tracer_provider()).get_tracer(SCOPE_NAME, VERSION) if tracing else None

    def workload(
        self,
        name: str,
        *,
        schedule: str | None = None,
        max_duration: Duration | None = None,
        expect_every: Duration | None = None,
        min_processed: int | None = None,
        failure_rate_threshold: float | None = None,
        severity: Severity | None = None,
    ) -> Workload:
        """Returns the workload called ``name``, creating it with the given expectations on first use. Call it once
        and keep the result. Passing expectations for a workload that already exists raises, so expectations are
        declared in exactly one place."""
        options = dict(schedule=schedule, max_duration=max_duration, expect_every=expect_every, min_processed=min_processed,
                       failure_rate_threshold=failure_rate_threshold, severity=severity)
        has_options = any(v is not None for v in options.values())
        with self._lock:
            existing = self._workloads.get(name)
            if existing is not None:
                if has_options:
                    raise WorkloadConfigurationError(
                        f"Workload '{name}' is already registered; set its options only where it is first created."
                    )
                return existing
            return self._add(name, Expectations(**options))

    def get_or_add(self, name: str, **options: Any) -> Workload:
        """Returns the workload called ``name``, creating it with ``options`` if it does not exist yet. Unlike
        ``workload``, an existing workload is returned as is: options declared elsewhere take precedence. Scheduler
        integrations use it to name workloads after jobs."""
        with self._lock:
            existing = self._workloads.get(name)
            if existing is not None:
                return existing
            return self._add(name, Expectations(**options))

    def _add(self, name: str, expectations: Expectations) -> Workload:
        validate_name(name)
        created = Workload(self, name, expectations)
        self._workloads[name] = created
        return created

    def monitor(self, name: str, **options: Any) -> Callable[[F], F]:
        """Decorates a function, sync or async, so every call is a run of the workload ``name``, declared with
        ``options``. The decorated function has a ``workload`` attribute, for counting items."""
        workload = self.workload(name, **options)
        return workload.monitor

    @property
    def workloads(self) -> list[Workload]:
        """Every workload created so far, sorted by name."""
        with self._lock:
            return sorted(self._workloads.values(), key=lambda w: w.name)

    def manifest(self, service: str) -> dict[str, Any]:
        """Describes every workload, for alert provisioning. ``service`` must match the OpenTelemetry ``service.name``."""
        entries = []
        for w in self.workloads:
            e = w.expectations
            entry: dict[str, Any] = {"name": w.name}
            if e.schedule is not None:
                entry["schedule"] = e.schedule
            if e.max_duration is not None:
                entry["maxDurationSeconds"] = _number(e.max_duration)
            if e.expect_every is not None:
                entry["expectEverySeconds"] = _number(e.expect_every)
            if e.min_processed:
                entry["minProcessed"] = e.min_processed
            if e.failure_rate_threshold:
                entry["failureRateThreshold"] = _number(e.failure_rate_threshold)
            entry["severity"] = e.severity
            entries.append(entry)
        return {"version": 1, "service": service, "workloads": entries}

    def write_manifest(self, path: str, service: str) -> None:
        """Writes the manifest as indented JSON, atomically, so a reader never sees half a manifest."""
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)
        with open(path + ".tmp", "w", encoding="utf-8") as f:
            f.write(json.dumps(self.manifest(service), indent=2, ensure_ascii=False))
        os.replace(path + ".tmp", path)

    def write_manifest_from_env(self, service: str | None = None) -> str | None:
        """Writes the manifest if the RUNSIGNAL_MANIFEST_PATH environment variable is set, so CI can ask for one
        without code changes. Call it once every workload has been registered. The service name is ``service``, or
        else OTEL_SERVICE_NAME, or else the script name. Returns the path written, or None."""
        path = os.environ.get("RUNSIGNAL_MANIFEST_PATH", "").strip()
        if not path:
            return None
        script = os.path.splitext(os.path.basename(sys.argv[0] if sys.argv and sys.argv[0] else "python"))[0]
        self.write_manifest(path, service or os.environ.get("OTEL_SERVICE_NAME") or script)
        return path

    def _observe_since_success(self, options: CallbackOptions) -> Iterable[Observation]:
        now = time.monotonic()
        for w in self.workloads:
            seconds = w._seconds_since_success(now)
            if seconds is not None:
                yield Observation(seconds, w._name_attributes)

    def _observe_since_heartbeat(self, options: CallbackOptions) -> Iterable[Observation]:
        now = time.monotonic()
        for w in self.workloads:
            seconds = w._seconds_since_heartbeat(now)
            if seconds is not None:
                yield Observation(seconds, w._name_attributes)


def _number(value: float) -> float | int:
    return int(value) if float(value).is_integer() else value


class Workload:
    """A unit of background work, such as a scheduled job, a queue consumer or a sync loop. Get one from a
    WorkloadRegistry once and keep it."""

    def __init__(self, registry: WorkloadRegistry, name: str, expectations: Expectations) -> None:
        self._registry = registry
        self.name = name
        self.expectations = expectations
        self._name_attributes = {Attributes.WORKLOAD: name}
        self._outcome_attributes = {
            outcome: {Attributes.WORKLOAD: name, Attributes.OUTCOME: outcome} for outcome in ("success", "failure", "cancelled")
        }
        self._processed = {Attributes.WORKLOAD: name, Attributes.ITEM_RESULT: "processed"}
        self._failed = {Attributes.WORKLOAD: name, Attributes.ITEM_RESULT: "failed"}
        # Until the first success or heartbeat these hold the time the workload was created, so a workload that
        # never succeeds or never beats still reports a growing age.
        self._last_success = self._last_heartbeat = time.monotonic()
        self._has_started = False
        self._has_beaten = False

    def start(self) -> Run:
        """Starts a run. End it with exactly one call to ``succeed``, ``fail``, ``fail_code`` or ``cancel``; later
        calls are ignored. Prefer ``run()`` as a context manager, which ends the run for you."""
        self._has_started = True
        tracer = self._registry._tracer
        span = tracer.start_span(self.name, kind=SpanKind.INTERNAL, attributes=self._name_attributes) if tracer else None
        return Run(self, time.monotonic(), span)

    def run(self) -> Run:
        """A run to use as a context manager, ``with`` or ``async with``. It succeeds when the block completes,
        is cancelled on ``asyncio.CancelledError``, ``KeyboardInterrupt`` or ``SystemExit``, and fails on any other
        exception, which propagates. The run's span is current inside the block."""
        return self.start()

    def monitor(self, fn: F) -> F:
        """Decorates a function, sync or async, so every call is a run of this workload."""
        if inspect.iscoroutinefunction(fn):
            @functools.wraps(fn)
            async def async_wrapper(*args: Any, **kwargs: Any) -> Any:
                with self.run():
                    return await fn(*args, **kwargs)
            async_wrapper.workload = self  # type: ignore[attr-defined]
            return async_wrapper  # type: ignore[return-value]

        @functools.wraps(fn)
        def wrapper(*args: Any, **kwargs: Any) -> Any:
            with self.run():
                return fn(*args, **kwargs)
        wrapper.workload = self  # type: ignore[attr-defined]
        return wrapper  # type: ignore[return-value]

    def processed(self, count: int = 1) -> None:
        """Records ``count`` successfully processed items. Recording 0 is useful: it reports that a run did nothing."""
        if count >= 0:
            self._registry._instruments.items.add(count, self._processed)

    def item_failed(self, count: int = 1) -> None:
        """Records ``count`` items that could not be processed."""
        if count >= 0:
            self._registry._instruments.items.add(count, self._failed)

    def beat(self) -> None:
        """Records a heartbeat: the workload is alive and making progress."""
        self._last_heartbeat = time.monotonic()
        self._has_beaten = True
        self._registry._instruments.heartbeats.add(1, self._name_attributes)

    def _complete(self, start: float, span: Span | None, outcome: str, error_type: str | None = None,
                  error: BaseException | None = None, description: str | None = None) -> None:
        now = time.monotonic()
        instruments = self._registry._instruments
        if error_type is None:
            instruments.runs.add(1, self._outcome_attributes[outcome])
        else:
            instruments.runs.add(1, {**self._outcome_attributes["failure"], Attributes.ERROR_TYPE: error_type})
        instruments.duration.record(now - start, self._outcome_attributes[outcome])
        if outcome == "success":
            self._last_success = now

        if span is not None:
            if span.is_recording():
                span.set_attribute(Attributes.OUTCOME, outcome)
                if outcome == "failure":
                    span.set_attribute(Attributes.ERROR_TYPE, error_type or "unknown")
                    if error is not None:
                        span.record_exception(error)
                    span.set_status(Status(StatusCode.ERROR, description))
                elif outcome == "success":
                    span.set_status(Status(StatusCode.OK))
            span.end()

    # Reported for workloads that are scheduled or have run, so alerts can tell a missed run from a workload that
    # simply has no runs.
    def _seconds_since_success(self, now: float) -> float | None:
        if self._has_started or self.expectations.schedule is not None:
            return now - self._last_success
        return None

    # Reported for workloads that expect heartbeats or have sent one.
    def _seconds_since_heartbeat(self, now: float) -> float | None:
        if self._has_beaten or self.expectations.expect_every is not None:
            return now - self._last_heartbeat
        return None

    def __repr__(self) -> str:
        return f"Workload({self.name!r})"


class Run:
    """One run of a Workload, from ``Workload.start`` or ``Workload.run``."""

    __slots__ = ("workload", "span", "_started", "_ended", "_token")

    def __init__(self, workload: Workload, started: float, span: Span | None) -> None:
        self.workload = workload
        self.span = span
        self._started = started
        self._ended = False
        self._token: object | None = None

    @property
    def elapsed(self) -> float:
        """Seconds since the run started."""
        return time.monotonic() - self._started

    def succeed(self) -> None:
        """Ends the run as successful."""
        if self._end():
            self.workload._complete(self._started, self.span, "success")

    def fail(self, error: BaseException) -> None:
        """Ends the run as failed because of ``error``. Its ``error.type`` is the exception's ``error_type``
        attribute if it has one, otherwise its fully qualified class name."""
        if self._end():
            self.workload._complete(self._started, self.span, "failure", _error_type(error), error, str(error) or None)

    def fail_code(self, code: str, description: str | None = None) -> None:
        """Ends the run as failed without an exception, for example when an expected input file is missing.
        ``code`` must be a short, stable value such as ``'statement_missing'``; never put messages or IDs in it."""
        if self._end():
            self.workload._complete(self._started, self.span, "failure", code or "unknown", None, description)

    def cancel(self) -> None:
        """Ends the run as cancelled, for example on shutdown. Cancelled runs do not count as failures."""
        if self._end():
            self.workload._complete(self._started, self.span, "cancelled")

    def _end(self) -> bool:
        if self._ended:
            return False
        self._ended = True
        return True

    def __enter__(self) -> Run:
        if self.span is not None:
            self._token = context.attach(trace.set_span_in_context(self.span))
        return self

    def __exit__(self, exc_type: Any, exc: BaseException | None, tb: Any) -> Literal[False]:
        if self._token is not None:
            context.detach(self._token)  # type: ignore[arg-type]
            self._token = None
        if exc is None:
            self.succeed()
        elif isinstance(exc, Exception):
            self.fail(exc)
        else:
            # asyncio.CancelledError, KeyboardInterrupt, SystemExit: the run was stopped, not broken.
            self.cancel()
        return False

    async def __aenter__(self) -> Run:
        return self.__enter__()

    async def __aexit__(self, exc_type: Any, exc: BaseException | None, tb: Any) -> Literal[False]:
        return self.__exit__(exc_type, exc, tb)


def _error_type(error: BaseException) -> str:
    error_type = getattr(error, "error_type", None)
    if isinstance(error_type, str) and error_type:
        return error_type
    cls = type(error)
    return cls.__qualname__ if cls.__module__ == "builtins" else f"{cls.__module__}.{cls.__qualname__}"


default_registry = WorkloadRegistry()
"""A process-wide registry on the global OpenTelemetry providers."""


def monitor(name: str, **options: Any) -> Callable[[F], F]:
    """Decorates a function, sync or async, so every call is a run of the workload ``name`` in the default
    registry, declared with ``options`` (``schedule``, ``max_duration``, ``expect_every``, ``min_processed``,
    ``failure_rate_threshold``, ``severity``)."""
    return default_registry.monitor(name, **options)

