from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable

import pytest
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import AggregationTemporality, HistogramDataPoint, InMemoryMetricReader
from opentelemetry.sdk.metrics import Counter, Histogram, ObservableGauge
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

from runsignal import WorkloadRegistry


@dataclass
class Point:
    value: float
    attributes: dict[str, Any]


@dataclass
class Harness:
    registry: WorkloadRegistry
    spans: InMemorySpanExporter
    reader: InMemoryMetricReader

    def collect(self) -> dict[str, list[Point]]:
        """Every data point since the last collection, per metric name. Histograms report their sum."""
        out: dict[str, list[Point]] = {}
        data = self.reader.get_metrics_data()
        for resource in data.resource_metrics if data else []:
            for scope in resource.scope_metrics:
                for metric in scope.metrics:
                    for dp in metric.data.data_points:
                        value = dp.sum if isinstance(dp, HistogramDataPoint) else dp.value
                        out.setdefault(metric.name, []).append(Point(value, dict(dp.attributes or {})))
        return out


def delta_reader() -> InMemoryMetricReader:
    delta = AggregationTemporality.DELTA
    return InMemoryMetricReader(preferred_temporality={Counter: delta, Histogram: delta, ObservableGauge: delta})


@pytest.fixture
def harness() -> Callable[..., Harness]:
    def make(**options: Any) -> Harness:
        reader = delta_reader()
        spans = InMemorySpanExporter()
        tracer_provider = TracerProvider()
        tracer_provider.add_span_processor(SimpleSpanProcessor(spans))
        registry = WorkloadRegistry(meter_provider=MeterProvider(metric_readers=[reader]), tracer_provider=tracer_provider, **options)
        return Harness(registry, spans, reader)

    return make
