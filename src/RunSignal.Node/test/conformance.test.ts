import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { it } from 'node:test';
import { AggregationTemporality, DataPointType, MeterProvider, MetricReader } from '@opentelemetry/sdk-metrics';
import { WorkloadRegistry, type Severity, type WorkloadOptions } from '../src/index.js';

class Reader extends MetricReader {
  constructor() {
    super({ aggregationTemporalitySelector: () => AggregationTemporality.CUMULATIVE });
  }
  protected override async onForceFlush(): Promise<void> {}
  protected override async onShutdown(): Promise<void> {}
}

interface Metric {
  name: string;
  type: string;
  unit: string;
  bounds?: number[];
  points: Array<{ attributes: Record<string, unknown>; value?: number; count?: number }>;
}

interface Spec {
  workloads: Array<{ name: string; schedule?: string; maxDurationSeconds?: number; expectEverySeconds?: number; minProcessed?: number; failureRateThreshold?: number; severity?: Severity }>;
  actions: Array<{ workload: string; action: string; code?: string; count?: number }>;
  expected: Metric[];
}

// Order-insensitive form: metrics sorted by name, points by their attributes.
function canonical(metrics: Metric[]): string {
  const sortKeys = (o: Record<string, unknown>) => Object.fromEntries(Object.entries(o).sort(([a], [b]) => (a < b ? -1 : 1)));
  return JSON.stringify(
    [...metrics]
      .sort((a, b) => (a.name < b.name ? -1 : 1))
      .map((m) => sortKeys({
        ...m,
        points: m.points
          .map((p) => sortKeys({ ...p, attributes: sortKeys(p.attributes) }))
          .sort((a, b) => (JSON.stringify(a['attributes']) < JSON.stringify(b['attributes']) ? -1 : 1)),
      })),
    null,
    2,
  );
}

// spec/conformance/signals.json: every SDK runs the same scenario and must emit exactly the same metrics.
it('the conformance scenario produces exactly the expected metrics', async () => {
  const spec = JSON.parse(readFileSync(new URL('../../../../spec/conformance/signals.json', import.meta.url), 'utf8')) as Spec;
  const reader = new Reader();
  const registry = new WorkloadRegistry({ meterProvider: new MeterProvider({ readers: [reader] }), tracing: false });

  for (const w of spec.workloads) {
    const options: WorkloadOptions = {
      ...(w.schedule !== undefined && { schedule: w.schedule }),
      ...(w.maxDurationSeconds !== undefined && { maxDuration: w.maxDurationSeconds * 1000 }),
      ...(w.expectEverySeconds !== undefined && { expectEvery: w.expectEverySeconds * 1000 }),
      ...(w.minProcessed !== undefined && { minProcessed: w.minProcessed }),
      ...(w.failureRateThreshold !== undefined && { failureRateThreshold: w.failureRateThreshold }),
      ...(w.severity !== undefined && { severity: w.severity }),
    };
    registry.workload(w.name, options);
  }

  for (const a of spec.actions) {
    const w = registry.workload(a.workload);
    switch (a.action) {
      case 'succeed': w.start().succeed(); break;
      case 'failCode': w.start().failCode(a.code!); break;
      case 'cancel': w.start().cancel(); break;
      case 'processed': w.processed(a.count!); break;
      case 'itemFailed': w.itemFailed(a.count!); break;
      case 'beat': w.beat(); break;
      default: throw new Error(`unknown action ${a.action}`);
    }
  }

  const { resourceMetrics } = await reader.collect();
  const got: Metric[] = [];
  for (const scope of resourceMetrics.scopeMetrics) {
    for (const metric of scope.metrics) {
      const type = { [DataPointType.SUM]: 'counter', [DataPointType.HISTOGRAM]: 'histogram', [DataPointType.GAUGE]: 'gauge' }[metric.dataPointType as number] ?? String(metric.dataPointType);
      const m: Metric = { name: metric.descriptor.name, type, unit: metric.descriptor.unit, points: [] };
      for (const dp of metric.dataPoints) {
        if (type === 'counter') m.points.push({ attributes: dp.attributes, value: dp.value as number });
        else if (type === 'histogram') {
          const h = dp.value as { count: number; buckets: { boundaries: number[] } };
          m.bounds = h.buckets.boundaries;
          m.points.push({ attributes: dp.attributes, count: h.count });
        } else m.points.push({ attributes: dp.attributes });
      }
      if (m.points.length > 0) got.push(m);
    }
  }

  assert.equal(canonical(got), canonical(spec.expected));
});
