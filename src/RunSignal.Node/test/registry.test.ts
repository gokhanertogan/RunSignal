import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, it } from 'node:test';
import { SpanStatusCode } from '@opentelemetry/api';
import {
  Attributes,
  InvalidWorkloadNameError,
  Metrics,
  toWorkloadName,
  WorkloadConfigurationError,
  WorkloadRegistry,
} from '../src/index.js';
import { harness } from './harness.js';

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe('runs', () => {
  it('records outcome, duration and a span', async () => {
    const { registry, spans, collect } = harness();
    const result = await registry.workload('nightly-invoice').run(() => 42);

    assert.equal(result, 42);
    const m = await collect();
    assert.deepEqual(m[Metrics.runs], [{ value: 1, attributes: { [Attributes.workload]: 'nightly-invoice', [Attributes.outcome]: 'success' } }]);
    assert.equal(m[Metrics.duration]?.length, 1);
    const [span] = spans.getFinishedSpans();
    assert.equal(span?.name, 'nightly-invoice');
    assert.equal(span?.status.code, SpanStatusCode.OK);
  });

  it('records the error type and rethrows', async () => {
    const cases: Array<[unknown, string]> = [
      [new TypeError('bad'), 'TypeError'],
      [Object.assign(new Error('refused'), { code: 'ECONNREFUSED' }), 'ECONNREFUSED'],
      [Object.assign(new Error('no file'), { errorType: 'statement_missing' }), 'statement_missing'],
      ['a string', 'string'],
    ];
    for (const [error, expected] of cases) {
      const { registry, collect } = harness();
      await assert.rejects(registry.workload('import').run(() => { throw error; }));
      const [runs] = (await collect())[Metrics.runs] ?? [];
      assert.equal(runs?.attributes[Attributes.outcome], 'failure');
      assert.equal(runs?.attributes[Attributes.errorType], expected);
    }
  });

  it('is cancelled only when its own signal was aborted', async () => {
    const { registry, collect } = harness();
    const workload = registry.workload('sync');
    const controller = new AbortController();

    await assert.rejects(workload.run(async (signal) => {
      controller.abort();
      signal?.throwIfAborted();
    }, { signal: controller.signal }));
    await assert.rejects(workload.run(() => { throw new DOMException('elsewhere', 'AbortError'); }));

    const outcomes = Object.fromEntries(((await collect())[Metrics.runs] ?? []).map((p) => [p.attributes[Attributes.outcome], p.value]));
    assert.deepEqual(outcomes, { cancelled: 1, failure: 1 });
  });

  it('records a run once and makes its span active inside run', async () => {
    const { registry, spans, collect } = harness();
    const run = registry.workload('once').start();
    run.succeed();
    run.fail(new Error('late'));
    run.cancel();
    assert.equal((await collect())[Metrics.runs]?.length, 1);

    const { trace } = await import('@opentelemetry/api');
    let activeSpanName: string | undefined;
    await registry.workload('nested').run(() => {
      activeSpanName = (trace.getActiveSpan() as unknown as { name: string } | undefined)?.name;
    });
    assert.equal(activeSpanName, 'nested');
    assert.equal(spans.getFinishedSpans().length, 2);
  });

  it('records no spans when tracing is off', async () => {
    const { registry, spans, collect } = harness({ tracing: false });
    await registry.workload('consumer').run(() => undefined);
    assert.equal((await collect())[Metrics.runs]?.length, 1);
    assert.equal(spans.getFinishedSpans().length, 0);
  });
});

describe('global providers', () => {
  it('follows a global meter provider registered after the registry was created', async () => {
    const { metrics } = await import('@opentelemetry/api');
    const { MeterProvider } = await import('@opentelemetry/sdk-metrics');
    const registry = new WorkloadRegistry(); // like an import that runs before the SDK starts
    const workload = registry.workload('early');

    const { collect, reader } = await import('./harness.js').then((h) => h.globalReader());
    metrics.setGlobalMeterProvider(new MeterProvider({ readers: [reader] }));
    try {
      await workload.run(() => undefined);
      assert.equal((await collect())[Metrics.runs]?.length, 1);
    } finally {
      metrics.disable();
    }
  });
});

describe('items, heartbeats and age gauges', () => {
  it('counts items and heartbeats and reports ages for workloads that need them', async () => {
    const { registry, collect } = harness();
    const scheduled = registry.workload('scheduled', { schedule: '0 2 * * *' });
    const loop = registry.workload('loop', { expectEvery: '5m' });
    registry.workload('bare');

    scheduled.processed(10);
    scheduled.itemFailed(2);
    await sleep(50);
    loop.beat();

    const m = await collect();
    const items = Object.fromEntries((m[Metrics.items] ?? []).map((p) => [p.attributes[Attributes.itemResult], p.value]));
    assert.deepEqual(items, { processed: 10, failed: 2 });
    assert.equal(m[Metrics.heartbeats]?.length, 1);

    // A scheduled workload reports its age before its first run; an unscheduled one does not.
    const since = m[Metrics.sinceSuccess] ?? [];
    assert.equal(since.length, 1);
    assert.equal(since[0]?.attributes[Attributes.workload], 'scheduled');
    assert.ok(since[0]!.value >= 0.04);
    const beat = m[Metrics.sinceHeartbeat] ?? [];
    assert.equal(beat.length, 1);
    assert.ok(beat[0]!.value < 0.04);
  });
});

describe('registry', () => {
  it('validates names and options and keeps options in one place', () => {
    const registry = new WorkloadRegistry();
    for (const bad of ['', 'Nightly', 'has space', '-dash', 'order/{id}']) {
      assert.throws(() => registry.workload(bad), InvalidWorkloadNameError);
    }
    assert.throws(() => registry.workload('rate', { failureRateThreshold: 1.5 }), WorkloadConfigurationError);
    assert.throws(() => registry.workload('soon', { maxDuration: 'soon' }), WorkloadConfigurationError);

    const first = registry.workload('sync', { severity: 'critical' });
    assert.equal(registry.workload('sync'), first);
    assert.throws(() => registry.workload('sync', { severity: 'info' }), WorkloadConfigurationError);
    assert.equal(registry.getOrAdd('sync', { severity: 'info' }).expectations.severity, 'critical');
  });

  it('matches the cross-language naming vectors', () => {
    const vectors = JSON.parse(readFileSync(new URL('../../../../spec/conformance/workload-names.json', import.meta.url), 'utf8')) as {
      names: Array<{ input: string; expected: string }>;
      invalid: string[];
    };
    for (const { input, expected } of vectors.names) assert.equal(toWorkloadName(input), expected, input);
    for (const input of vectors.invalid) assert.throws(() => toWorkloadName(input), InvalidWorkloadNameError);
  });

  it('writes the same manifest as the other SDKs', () => {
    const registry = new WorkloadRegistry();
    registry.workload('stock-sync', { expectEvery: '5m', failureRateThreshold: 0.2 });
    registry.workload('nightly-invoice', { schedule: '0 2 * * *', maxDuration: 30 * 60_000, minProcessed: 100, severity: 'critical' });

    const path = join(mkdtempSync(join(tmpdir(), 'runsignal-')), 'out', 'manifest.json');
    process.env.RUNSIGNAL_MANIFEST_PATH = path;
    try {
      assert.equal(registry.writeManifestFromEnv('billing'), path);
    } finally {
      delete process.env.RUNSIGNAL_MANIFEST_PATH;
    }

    // Byte for byte what the Go SDK writes for the same workloads.
    assert.equal(readFileSync(path, 'utf8'), `{
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
}`);
  });
});
