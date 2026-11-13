from __future__ import annotations

import asyncio
import json
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


@pytest.mark.parametrize("owner", ["", "   ", "x" * 129])
def test_a_blank_or_too_long_owner_is_rejected(owner: str) -> None:
    with pytest.raises(WorkloadConfigurationError):
        WorkloadRegistry().workload("owned", owner=owner)
    with pytest.raises(WorkloadConfigurationError):
        WorkloadRegistry().get_or_add("owned", owner=owner)


@pytest.mark.parametrize("url", [
    "", "wiki/billing", "/runbooks/billing", "ftp://example.com/runbook", "https://", "HTTPS://example.com",
    "https://example.com/a b", "mailto:team@example.com",
])
def test_a_runbook_url_that_is_not_absolute_http_is_rejected(url: str) -> None:
    with pytest.raises(WorkloadConfigurationError):
        WorkloadRegistry().workload("runbook", runbook_url=url)
    with pytest.raises(WorkloadConfigurationError):
        WorkloadRegistry().monitor("runbook", runbook_url=url)


def test_owner_and_runbook_url_are_written_after_severity_and_omitted_when_unset() -> None:
    registry = WorkloadRegistry()
    registry.workload("nightly-invoice", runbook_url="https://wiki.example.com/runbooks/nightly-invoice",
                      owner="billing-team", severity="critical", schedule="0 2 * * *")
    registry.get_or_add("only-owner", owner="x" * 128)

    @registry.monitor("decorated", owner="Jane Doe", runbook_url="http://wiki.local/decorated")
    def decorated() -> None:
        pass

    registry.workload("plain")

    assert json.dumps(registry.manifest("billing"), indent=2, ensure_ascii=False) == """{
  "version": 1,
  "service": "billing",
  "workloads": [
    {
      "name": "decorated",
      "severity": "warning",
      "owner": "Jane Doe",
      "runbookUrl": "http://wiki.local/decorated"
    },
    {
      "name": "nightly-invoice",
      "schedule": "0 2 * * *",
      "severity": "critical",
      "owner": "billing-team",
      "runbookUrl": "https://wiki.example.com/runbooks/nightly-invoice"
    },
    {
      "name": "only-owner",
      "severity": "warning",
      "owner": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
    },
    {
      "name": "plain",
      "severity": "warning"
    }
  ]
}"""


def test_no_manifest_without_the_environment_variable(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("RUNSIGNAL_MANIFEST_PATH", raising=False)
    assert WorkloadRegistry().write_manifest_from_env("billing") is None


def _ages(h) -> dict[str, float]:
    return {p.attributes[Attributes.WORKLOAD]: p.value for p in h.collect().get(Metrics.ACTIVE_RUN_AGE, [])}


def test_active_run_age_grows_while_a_run_is_in_progress_and_returns_to_zero(harness) -> None:
    h = harness()
    w = h.registry.workload("nightly-invoice")
    run = w.start()
    run._started -= 5  # as if it started 5 seconds ago

    assert _ages(h)["nightly-invoice"] >= 5

    run.succeed()
    assert _ages(h) == {"nightly-invoice": 0}

    with w.run():
        assert _ages(h)["nightly-invoice"] > 0
    assert _ages(h) == {"nightly-invoice": 0}


def test_active_run_age_is_reported_for_workloads_with_a_max_duration_or_runs(harness) -> None:
    h = harness()
    h.registry.workload("idle")
    h.registry.workload("nightly-invoice", max_duration="30m")
    h.registry.workload("stock-sync", schedule="*/5 * * * *", expect_every="5m")

    assert _ages(h) == {"nightly-invoice": 0}


def test_active_run_age_reports_the_oldest_run_and_a_double_end_does_not_end_another_run(harness) -> None:
    h = harness()
    w = h.registry.workload("import-statements")
    first = w.start()
    first.succeed()
    second = w.start()  # reuses the slot the first run freed
    second._started -= 60
    third = w.start()
    third._started -= 10

    first.succeed()
    first.cancel()
    assert _ages(h)["import-statements"] >= 60

    second.fail_code("statement_missing")
    age = _ages(h)["import-statements"]
    assert 10 <= age < 60

    third.cancel()
    assert _ages(h) == {"import-statements": 0}


def test_active_run_age_tracks_concurrent_runs_from_many_threads(harness) -> None:
    import threading

    h = harness()
    w = h.registry.workload("sync")
    runs = []
    lock = threading.Lock()

    def start() -> None:
        for _ in range(8):
            r = w.start()
            with lock:
                runs.append(r)

    threads = [threading.Thread(target=start) for _ in range(8)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert len({r._slot for r in runs}) == 64

    extra = w.start()  # beyond 64 concurrent runs: not tracked, ending it is harmless
    assert extra._slot == -1
    extra.succeed()
    for r in runs[:-1]:
        r.succeed()
    assert _ages(h)["sync"] > 0
    runs[-1].succeed()
    assert _ages(h) == {"sync": 0}


def test_registries_sharing_a_meter_provider_all_report_their_gauges() -> None:
    # OpenTelemetry keeps only the first gauge registered under a name, so each registry's own gauges would drop
    # the other's workloads, and with them their missed and hung alerts.
    from opentelemetry.sdk.metrics import MeterProvider
    from opentelemetry.sdk.metrics.export import InMemoryMetricReader

    from runsignal import WorkloadRegistry

    reader = InMemoryMetricReader()
    provider = MeterProvider(metric_readers=[reader])
    first = WorkloadRegistry(meter_provider=provider, tracing=False)
    second = WorkloadRegistry(meter_provider=provider, tracing=False)
    first.workload("shared-first", schedule="0 2 * * *", max_duration=60)
    second.workload("shared-second", schedule="0 2 * * *", max_duration=60)

    data = reader.get_metrics_data()
    reported = {
        m.name: {dp.attributes["runsignal.workload"] for dp in m.data.data_points}
        for rm in data.resource_metrics
        for sm in rm.scope_metrics
        for m in sm.metrics
    }
    assert reported["runsignal.workload.since_success"] == {"shared-first", "shared-second"}
    assert reported["runsignal.workload.active_run_age"] == {"shared-first", "shared-second"}
