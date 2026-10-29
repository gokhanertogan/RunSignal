import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { Attributes, Metrics, WorkloadConfigurationError } from '../src/index.js';
import { cronJob } from '../src/cron.js';
import { harness } from './harness.js';

describe('cronJob', () => {
  it('registers the workload with its schedule before the first tick', () => {
    const { registry } = harness();
    const job = cronJob('nightly-invoice', { schedule: '0 2 * * *', maxDuration: '30m', registry }, () => {});

    assert.equal(job.schedule, '0 2 * * *');
    assert.deepEqual(registry.manifest('billing').workloads, [{ name: 'nightly-invoice', schedule: '0 2 * * *', maxDurationSeconds: 1800, severity: 'warning' }]);
  });

  it('records each tick as a run and counts items', async () => {
    const { registry, collect } = harness();
    const job = cronJob('nightly-invoice', { schedule: '0 2 * * *', registry }, (w) => w.processed(3));

    await job.run();

    const m = await collect();
    assert.deepEqual(m[Metrics.runs], [{ value: 1, attributes: { [Attributes.workload]: 'nightly-invoice', [Attributes.outcome]: 'success' } }]);
    assert.equal(m[Metrics.items]?.[0]?.value, 3);
  });

  it('records a failed tick and hands the error to onError instead of rejecting', async () => {
    const { registry, collect } = harness();
    const errors: unknown[] = [];
    const failure = Object.assign(new Error('no file'), { errorType: 'statement_missing' });
    const job = cronJob('import', { schedule: '*/5 * * * *', registry, onError: (e) => errors.push(e) }, async () => { throw failure; });

    await job.run();

    assert.deepEqual(errors, [failure]);
    const [runs] = (await collect())[Metrics.runs] ?? [];
    assert.equal(runs?.attributes[Attributes.outcome], 'failure');
    assert.equal(runs?.attributes[Attributes.errorType], 'statement_missing');
  });

  it('rejects a second declaration of the same job', () => {
    const { registry } = harness();
    cronJob('import', { schedule: '0 * * * *', registry }, () => {});
    assert.throws(() => cronJob('import', { schedule: '0 * * * *', registry }, () => {}), WorkloadConfigurationError);
  });
});
