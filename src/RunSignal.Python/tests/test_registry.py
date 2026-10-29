from __future__ import annotations

import asyncio
import os
from datetime import timedelta
from pathlib import Path

import pytest
from opentelemetry import trace
from opentelemetry.trace import StatusCode

from runsignal import Attributes, InvalidWorkloadNameError, Metrics, WorkloadConfigurationError, WorkloadRegistry


class StatementMissing(Exception):
    error_type = "statement_missing"


def test_a_run_records_outcome_duration_and_a_span(harness) -> None:
    h = harness()
    with h.registry.workload("nightly-invoice").run():
        pass

    m = h.collect()
    assert [(p.value, p.attributes) for p in m[Metrics.RUNS]] == [
        (1, {Attributes.WORKLOAD: "nightly-invoice", Attributes.OUTCOME: "success"})
    ]
    assert len(m[Metrics.DURATION]) == 1
    [span] = h.spans.get_finished_spans()
    assert span.name == "nightly-invoice"
    assert span.status.status_code == StatusCode.OK


@pytest.mark.parametrize(
    "error, expected",
    [
        (ValueError("bad"), "ValueError"),
        (StatementMissing("no file"), "statement_missing"),
        (asyncio.InvalidStateError("x"), "asyncio.exceptions.InvalidStateError"),
    ],
)
def test_a_failed_run_records_the_error_type_and_reraises(harness, error, expected) -> None:
    h = harness()
    with pytest.raises(type(error)):
        with h.registry.workload("import").run():
            raise error

    [runs] = h.collect()[Metrics.RUNS]
    assert runs.attributes[Attributes.OUTCOME] == "failure"
    assert runs.attributes[Attributes.ERROR_TYPE] == expected
    [span] = h.spans.get_finished_spans()
    assert span.status.status_code == StatusCode.ERROR
    assert span.events[0].name == "exception"


def test_cancellation_and_interrupts_are_not_failures(harness) -> None:
    h = harness()
    workload = h.registry.workload("sync")

    async def cancelled() -> None:
        async with workload.run():
            raise asyncio.CancelledError()

    with pytest.raises(asyncio.CancelledError):
        asyncio.run(cancelled())
    with pytest.raises(KeyboardInterrupt):
        with workload.run():
            raise KeyboardInterrupt()

    [runs] = h.collect()[Metrics.RUNS]
    assert (runs.value, runs.attributes[Attributes.OUTCOME]) == (2, "cancelled")


def test_a_run_ends_once_and_its_span_is_current_inside(harness) -> None:
    h = harness()
    with h.registry.workload("import").run() as run:
        assert trace.get_current_span() is run.span
        run.fail_code("statement_missing", "no statement today")
    run.succeed()

    [runs] = h.collect()[Metrics.RUNS]
    assert (runs.value, runs.attributes[Attributes.ERROR_TYPE]) == (1, "statement_missing")
    assert trace.get_current_span() is not run.span


def test_monitor_decorates_sync_and_async_functions(harness) -> None:
    h = harness()

    @h.registry.monitor("sync-job", schedule="*/5 * * * *")
    def sync_job(n: int) -> int:
        sync_job.workload.processed(n)
        return n * 2

    @h.registry.monitor("async-job")
    async def async_job() -> str:
        return "done"

    assert sync_job(3) == 6
    assert asyncio.run(async_job()) == "done"
    assert sync_job.__name__ == "sync_job"

    m = h.collect()
    assert sorted(p.attributes[Attributes.WORKLOAD] for p in m[Metrics.RUNS]) == ["async-job", "sync-job"]
    assert m[Metrics.ITEMS][0].value == 3


def test_no_spans_when_tracing_is_off(harness) -> None:
    h = harness(tracing=False)
    with h.registry.workload("hot-loop").run() as run:
        assert run.span is None
    assert h.spans.get_finished_spans() == ()


def test_items_heartbeats_and_age_gauges(harness) -> None:
    h = harness()
    scheduled = h.registry.workload("scheduled", schedule="0 2 * * *")
    loop = h.registry.workload("loop", expect_every=60)
    h.registry.workload("idle")
    scheduled.processed(5)
    scheduled.item_failed()
    loop.beat()

    m = h.collect()
    assert {p.attributes[Attributes.ITEM_RESULT]: p.value for p in m[Metrics.ITEMS]} == {"processed": 5, "failed": 1}
    assert m[Metrics.HEARTBEATS][0].value == 1
    assert [p.attributes[Attributes.WORKLOAD] for p in m[Metrics.SINCE_SUCCESS]] == ["scheduled"]
    assert [p.attributes[Attributes.WORKLOAD] for p in m[Metrics.SINCE_HEARTBEAT]] == ["loop"]


def test_names_and_options_are_validated_and_declared_once() -> None:
    registry = WorkloadRegistry()
    with pytest.raises(InvalidWorkloadNameError):
        registry.workload("Not Valid")
    with pytest.raises(WorkloadConfigurationError):
        registry.workload("rate", failure_rate_threshold=2)
    with pytest.raises(WorkloadConfigurationError):
        registry.workload("duration", max_duration="soon")
    with pytest.raises(WorkloadConfigurationError):
        registry.workload("severity", severity="urgent")  # type: ignore[arg-type]

    first = registry.workload("import", schedule="0 * * * *")
    assert registry.workload("import") is first
    assert registry.get_or_add("import", schedule="0 3 * * *") is first
    with pytest.raises(WorkloadConfigurationError):
        registry.workload("import", schedule="0 3 * * *")


def test_writes_the_same_manifest_as_the_other_sdks(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    registry = WorkloadRegistry()
    registry.workload("stock-sync", expect_every="5m", failure_rate_threshold=0.2)
    registry.workload("nightly-invoice", schedule="0 2 * * *", max_duration=timedelta(minutes=30), min_processed=100,
                      severity="critical")

    path = tmp_path / "out" / "manifest.json"
    monkeypatch.setenv("RUNSIGNAL_MANIFEST_PATH", str(path))
    assert registry.write_manifest_from_env("billing") == str(path)
    assert not os.path.exists(str(path) + ".tmp")

    # Byte for byte what the Go and Node.js SDKs write for the same workloads.
    assert path.read_text(encoding="utf-8") == """{
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
}"""


def test_no_manifest_without_the_environment_variable(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("RUNSIGNAL_MANIFEST_PATH", raising=False)
    assert WorkloadRegistry().write_manifest_from_env("billing") is None
