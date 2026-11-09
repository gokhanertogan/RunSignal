import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { diag, DiagLogLevel, metrics } from '@opentelemetry/api';
import { AggregationTemporality, MeterProvider, MetricReader } from '@opentelemetry/sdk-metrics';
import { Attributes, defaultRegistry, Metrics, WorkloadRegistry } from '../src/index.js';

class Reader extends MetricReader {
  constructor() {
    super({ aggregationTemporalitySelector: () => AggregationTemporality.CUMULATIVE });
  }
  protected override async onForceFlush(): Promise<void> {}
  protected override async onShutdown(): Promise<void> {}
}

const expectations = { schedule: '0 2 * * *', maxDuration: '10m' } as const;

/** Captures what OpenTelemetry warns about meanwhile, such as duplicate instrument registrations. */
function captureWarnings() {
  const warnings: string[] = [];
  const log = (...args: unknown[]) => warnings.push(args.map(String).join(' '));
  diag.setLogger({ error: log, warn: log, info: () => {}, debug: () => {}, verbose: () => {} }, { logLevel: DiagLogLevel.WARN, suppressOverrideMessage: true });
  return { warnings, restore: () => diag.disable() };
}

async function assertBothWorkloads(reader: MetricReader, first: string, second: string): Promise<void> {
  const { resourceMetrics, errors } = await reader.collect();
  assert.deepEqual(errors, []);
  const names = resourceMetrics.scopeMetrics.flatMap((s) => s.metrics.map((m) => m.descriptor.name));
  assert.deepEqual(names.filter((n, i) => names.indexOf(n) !== i), [], 'no metric is exported as two streams');
  for (const name of [Metrics.sinceSuccess, Metrics.activeRunAge]) {
    const streams = resourceMetrics.scopeMetrics.flatMap((s) => s.metrics).filter((m) => m.descriptor.name === name);
    assert.equal(streams.length, 1, `${name} streams`);
    const workloads = streams[0]!.dataPoints.map((dp) => dp.attributes[Attributes.workload]).sort();
    assert.deepEqual(workloads, [first, second].sort(), `${name} workloads`);
  }
}

// OpenTelemetry keeps only the first instrument registered under a name on a meter in some SDKs. Every registry on
// a provider must still report its own workloads, in one stream per metric.
describe('registries sharing a meter provider', () => {
  it('both report their gauges on an explicit provider', async () => {
    const { warnings, restore } = captureWarnings();
    try {
      const reader = new Reader();
      const meterProvider = new MeterProvider({ readers: [reader] });
      new WorkloadRegistry({ meterProvider }).workload('first', expectations);
      new WorkloadRegistry({ meterProvider, tracing: false }).workload('second', expectations);
      await assertBothWorkloads(reader, 'first', 'second');
      assert.deepEqual(warnings, []);
    } finally {
      restore();
    }
  });

  it('the default registry and a user registry both report their gauges on the global provider', async () => {
    const { warnings, restore } = captureWarnings();
    const reader = new Reader();
    metrics.setGlobalMeterProvider(new MeterProvider({ readers: [reader] }));
    try {
      defaultRegistry.workload('shared-default', expectations);
      new WorkloadRegistry().workload('shared-user', expectations);
      await assertBothWorkloads(reader, 'shared-default', 'shared-user');
      assert.deepEqual(warnings, []);
    } finally {
      metrics.disable();
      restore();
    }
  });
});
