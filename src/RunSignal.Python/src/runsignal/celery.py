"""
Celery integration: records every task execution as a run of its workload, with no code in your tasks, and
registers Celery beat's crontab entries as workloads with their schedules, so a periodic task that never runs
still gets a missed alert.

    from runsignal.celery import instrument

    app = Celery("billing")
    app.conf.beat_schedule = {"nightly-invoice": {"task": "billing.tasks.generate_invoices",
                                                   "schedule": crontab(hour=2, minute=0)}}
    instrument(app, workloads={"billing.tasks.generate_invoices": {"max_duration": "30m", "severity": "critical"}})
"""

from __future__ import annotations

import os
import threading
from typing import Any, Mapping

from celery import Celery, Task, signals
from celery.schedules import crontab

from ._naming import to_workload_name
from ._registry import Run, Workload, WorkloadRegistry, default_registry

__all__ = ["instrument", "discover_beat_schedule", "workload_name_of", "workload_of", "beat_cron"]


def workload_name_of(task: Task | str) -> str:
    """The workload a task runs as: its task name, such as ``billing.tasks.generate_invoices``."""
    return to_workload_name(task if isinstance(task, str) else task.name)


def beat_cron(schedule: crontab) -> str:
    """A Celery ``crontab`` as a 5-field cron expression. Celery numbers days of the week like Unix cron
    (0 = Sunday), so fields carry over unchanged."""

    def field(value: Any) -> str:
        if isinstance(value, (set, frozenset, list, tuple)):
            return ",".join(str(v) for v in sorted(value))
        return str(value)

    return " ".join(field(getattr(schedule, f"_orig_{name}")) for name in
                    ("minute", "hour", "day_of_month", "month_of_year", "day_of_week"))


def discover_beat_schedule(
    app: Celery,
    *,
    registry: WorkloadRegistry | None = None,
    workloads: Mapping[str, Mapping[str, Any]] | None = None,
) -> list[Workload]:
    """Registers a workload for every task in ``app.conf.beat_schedule``, with its crontab as the schedule, so a
    periodic task that never runs is still alerted on. Entries with an interval instead of a crontab get no
    schedule. Time zones are not carried over: pass ``--time-zone`` to the CLI if beat uses one."""
    registry = registry or default_registry
    found: list[Workload] = []
    for entry in (app.conf.beat_schedule or {}).values():
        task = entry.get("task")
        if not task or task.startswith("celery."):
            continue
        name = workload_name_of(task)
        options = dict((workloads or {}).get(name, {}))
        schedule = entry.get("schedule")
        if isinstance(schedule, crontab):
            options.setdefault("schedule", beat_cron(schedule))
        found.append(registry.get_or_add(name, **options))
    return found


_task_workloads: dict[str, Workload] = {}


def workload_of(task: Task) -> Workload | None:
    """The workload a task runs as, for counting items: ``workload_of(self).processed()`` in a bound task."""
    return _task_workloads.get(task.name)


class _Instrumentation:
    def __init__(self, registry: WorkloadRegistry, workloads: Mapping[str, Mapping[str, Any]]) -> None:
        self.registry = registry
        self.workloads = workloads
        self.runs: dict[str, Run] = {}
        self.errors: dict[str, BaseException] = {}
        self.lock = threading.Lock()

    def workload(self, task: Task) -> Workload | None:
        workload = _task_workloads.get(task.name)
        if workload is None:
            if task.name.startswith("celery."):
                return None
            name = workload_name_of(task)
            workload = self.registry.get_or_add(name, **self.workloads.get(name, {}))
            _task_workloads[task.name] = workload
        return workload

    def prerun(self, task_id: str = "", task: Task | None = None, **_: Any) -> None:
        workload = self.workload(task) if task is not None else None
        if workload is not None:
            with self.lock:
                self.runs[task_id] = workload.start()

    def failure(self, task_id: str = "", exception: BaseException | None = None, **_: Any) -> None:
        if exception is not None:
            with self.lock:
                self.errors[task_id] = exception

    def retry(self, request: Any = None, reason: Any = None, **_: Any) -> None:
        if request is not None and isinstance(reason, BaseException):
            with self.lock:
                self.errors[request.id] = reason

    def postrun(self, task_id: str = "", state: str | None = None, **_: Any) -> None:
        with self.lock:
            run = self.runs.pop(task_id, None)
            error = self.errors.pop(task_id, None)
        if run is None:
            return
        if state == "SUCCESS":
            run.succeed()
        elif state in ("FAILURE", "RETRY"):
            if error is not None:
                run.fail(error)
            else:
                run.fail_code(state.lower())
        else:
            run.cancel()

    def revoked(self, request: Any = None, **_: Any) -> None:
        if request is None:
            return
        with self.lock:
            run = self.runs.pop(request.id, None)
            self.errors.pop(request.id, None)
        if run is not None:
            run.cancel()


_instrumented: dict[int, _Instrumentation] = {}


def instrument(
    app: Celery | None = None,
    *,
    registry: WorkloadRegistry | None = None,
    workloads: Mapping[str, Mapping[str, Any]] | None = None,
    write_manifest_on_ready: bool = True,
) -> list[Workload]:
    """Records every Celery task execution as a run: success, failure with the exception's type (each retry counts
    as a failed run), and cancellation when the task is revoked or ignored. Celery's own ``celery.*`` tasks are
    skipped. Call it once per process, where the app is configured.

    With ``app``, it also registers the beat schedule's tasks (``discover_beat_schedule``) and, when
    ``write_manifest_on_ready`` is true, writes the manifest when the worker is ready if RUNSIGNAL_MANIFEST_PATH is
    set. ``workloads`` maps workload names to expectations, such as ``{"billing.tasks.generate_invoices":
    {"max_duration": "30m"}}``. Returns the workloads discovered from the beat schedule.
    """
    registry = registry or default_registry
    workloads = workloads or {}
    if id(registry) not in _instrumented:
        handler = _Instrumentation(registry, workloads)
        _instrumented[id(registry)] = handler
        signals.task_prerun.connect(handler.prerun, weak=False)
        signals.task_failure.connect(handler.failure, weak=False)
        signals.task_retry.connect(handler.retry, weak=False)
        signals.task_postrun.connect(handler.postrun, weak=False)
        signals.task_revoked.connect(handler.revoked, weak=False)

    if app is None:
        return []
    found = discover_beat_schedule(app, registry=registry, workloads=workloads)
    if write_manifest_on_ready:
        def write_manifest(**_: Any) -> None:
            registry.write_manifest_from_env(os.environ.get("OTEL_SERVICE_NAME") or app.main or None)
        signals.worker_ready.connect(write_manifest, weak=False)
    return found
