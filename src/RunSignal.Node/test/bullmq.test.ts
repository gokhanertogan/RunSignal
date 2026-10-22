import assert from 'node:assert/strict';
import { spawn, type ChildProcess } from 'node:child_process';
import { after, before, describe, it } from 'node:test';
import { Queue, Worker } from 'bullmq';
import { Redis } from 'ioredis';
import { discoverSchedulers, instrument, workloadOf } from '../src/bullmq.js';
import { Attributes, Metrics } from '../src/index.js';
import { harness } from './harness.js';

// A real Redis, because BullMQ runs Lua scripts that mocks do not support. Requires redis-server on the PATH.
// BullMQ 6 in ESM needs a constructed client rather than connection options.
const port = 6390 + Math.floor(Math.random() * 100);
const clients: Redis[] = [];
const connection = () => {
  const client = new Redis({ host: '127.0.0.1', port, maxRetriesPerRequest: null, lazyConnect: false });
  clients.push(client);
  return client;
};
let redis: ChildProcess;

const waitFor = async (condition: () => boolean | Promise<boolean>, ms = 10_000) => {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    if (await condition()) return;
    await new Promise((r) => setTimeout(r, 50));
  }
  throw new Error('timed out');
};

before(async () => {
  redis = spawn('redis-server', ['--port', String(port), '--save', '', '--appendonly', 'no'], { stdio: 'ignore' });
  const probe = new Queue('probe', { connection: connection() });
  await waitFor(async () => { try { await probe.getJobCounts(); return true; } catch { return false; } });
  await probe.close();
});

after(async () => {
  await Promise.all(clients.map((c) => c.quit().catch(() => undefined)));
  redis.kill();
});

describe('BullMQ', () => {
  it('records each job as a run and passes results and errors through', async () => {
    const { registry, collect } = harness();
    const queue = new Queue('emails', { connection: connection() });
    const results: unknown[] = [];
    const worker = new Worker<{ fail: boolean }>('emails', instrument(async (job) => {
      if (job.data.fail) throw Object.assign(new Error('smtp down'), { code: 'ESMTP' });
      workloadOf(job)?.processed(3);
      return 'sent';
    }, { registry, workloads: { 'emails.send-welcome': { severity: 'critical' } } }), { connection: connection() });
    worker.on('completed', (_job, result) => results.push(result));
    worker.on('failed', (_job, error) => results.push(error.message));

    await queue.add('send-welcome', { fail: false });
    await queue.add('send-welcome', { fail: true }, { attempts: 1 });
    await waitFor(() => results.length === 2);
    await worker.close();
    await queue.obliterate({ force: true });
    await queue.close();

    assert.deepEqual(results.sort(), ['sent', 'smtp down']);
    const m = await collect();
    const runs = Object.fromEntries((m[Metrics.runs] ?? []).map((p) => [`${p.attributes[Attributes.outcome]}:${p.attributes[Attributes.errorType] ?? ''}`, p.value]));
    assert.deepEqual(runs, { 'success:': 1, 'failure:ESMTP': 1 });
    assert.equal(m[Metrics.items]?.[0]?.value, 3);
    assert.equal(registry.workload('emails.send-welcome').expectations.severity, 'critical');
  });

  it('names scheduled jobs after their scheduler and discovers schedulers before they fire', async () => {
    const { registry, collect } = harness();
    const queue = new Queue('reports', { connection: connection() });
    await queue.upsertJobScheduler('Daily Sales Report', { pattern: '0 0 6 * * *' }, { name: 'build' });
    await queue.upsertJobScheduler('price-refresh', { every: 1_000 }, { name: 'refresh' });

    const discovered = await discoverSchedulers(queue, { registry, workloads: { 'daily-sales-report': { maxDuration: '10m' } } });
    assert.deepEqual(discovered.map((w) => w.name).sort(), ['daily-sales-report', 'price-refresh']);
    const daily = registry.workload('daily-sales-report').expectations;
    assert.equal(daily.schedule, '0 0 6 * * *');
    assert.equal(daily.maxDurationMs, 600_000);
    assert.equal(registry.workload('price-refresh').expectations.schedule, undefined); // "every" is not a cron

    // A job the scheduler produces runs as the scheduler's workload.
    let ran = false;
    const worker = new Worker('reports', instrument(async () => { ran = true; }, { registry }), { connection: connection() });
    await waitFor(() => ran);
    await worker.close();
    await queue.obliterate({ force: true });
    await queue.close();

    const runs = (await collect())[Metrics.runs] ?? [];
    assert.ok(runs.some((p) => p.attributes[Attributes.workload] === 'price-refresh'), JSON.stringify(runs));
  });
});
