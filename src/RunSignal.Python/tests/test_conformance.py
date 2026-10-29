from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import Gauge, Histogram, InMemoryMetricReader, Sum

from runsignal import InvalidWorkloadNameError, WorkloadRegistry, to_workload_name

SPEC = Path(__file__).resolve().parents[3] / "spec" / "conformance"


def test_naming_vectors() -> None:
    vectors = json.loads((SPEC / "workload-names.json").read_text(encoding="utf-8"))
    for case in vectors["names"]:
        assert to_workload_name(case["input"]) == case["expected"], case["input"]
    for invalid in vectors["invalid"]:
        with pytest.raises(InvalidWorkloadNameError):
            to_workload_name(invalid)


def _canonical(metrics: list[dict[str, Any]]) -> str:
    def point_key(p: dict[str, Any]) -> str:
        return json.dumps(p["attributes"], sort_keys=True)

    return json.dumps(
        sorted(({**m, "points": sorted(m["points"], key=point_key)} for m in metrics), key=lambda m: m["name"]),
        sort_keys=True,
        indent=2,
    )


# spec/conformance/signals.json: every SDK runs the same scenario and must emit exactly the same metrics.
def test_signals_scenario_produces_exactly_the_expected_metrics() -> None:
    spec = json.loads((SPEC / "signals.json").read_text(encoding="utf-8"))
    reader = InMemoryMetricReader()  # cumulative, like the other SDKs' conformance tests
    registry = WorkloadRegistry(meter_provider=MeterProvider(metric_readers=[reader]), tracing=False)

    for w in spec["workloads"]:
        registry.workload(
            w["name"],
            schedule=w.get("schedule"),
            max_duration=w.get("maxDurationSeconds"),
            expect_every=w.get("expectEverySeconds"),
            min_processed=w.get("minProcessed"),
            failure_rate_threshold=w.get("failureRateThreshold"),
            severity=w.get("severity"),
        )

    for a in spec["actions"]:
        w = registry.workload(a["workload"])
        action = a["action"]
        if action == "succeed":
            w.start().succeed()
        elif action == "failCode":
            w.start().fail_code(a["code"])
        elif action == "cancel":
            w.start().cancel()
        elif action == "processed":
            w.processed(a["count"])
        elif action == "itemFailed":
            w.item_failed(a["count"])
        elif action == "beat":
            w.beat()
        else:
            raise AssertionError(f"unknown action {action}")

    got: list[dict[str, Any]] = []
    data = reader.get_metrics_data()
    for resource in data.resource_metrics:
        for scope in resource.scope_metrics:
            for metric in scope.metrics:
                m: dict[str, Any] = {"name": metric.name, "unit": metric.unit, "points": []}
                if isinstance(metric.data, Sum):
                    m["type"] = "counter"
                    m["points"] = [{"attributes": dict(dp.attributes), "value": dp.value} for dp in metric.data.data_points]
                elif isinstance(metric.data, Histogram):
                    m["type"] = "histogram"
                    for dp in metric.data.data_points:
                        m["bounds"] = list(dp.explicit_bounds)
                        m["points"].append({"attributes": dict(dp.attributes), "count": dp.count})
                elif isinstance(metric.data, Gauge):
                    m["type"] = "gauge"
                    m["points"] = [{"attributes": dict(dp.attributes)} for dp in metric.data.data_points]
                if m["points"]:
                    got.append(m)

    assert _canonical(got) == _canonical(spec["expected"])
