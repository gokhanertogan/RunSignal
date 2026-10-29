# RunSignal for Node.js

[![npm](https://img.shields.io/npm/v/@runsignal/node)](https://www.npmjs.com/package/@runsignal/node)
[![npm downloads](https://img.shields.io/npm/dm/@runsignal/node)](https://www.npmjs.com/package/@runsignal/node)
[![license](https://img.shields.io/npm/l/@runsignal/node)](https://github.com/gokhanertogan/RunSignal/blob/main/LICENSE)

**Cron job and background job monitoring for Node.js, on OpenTelemetry.** Get an alert when a scheduled job
does not run, fails, runs too long or processes nothing, and when a BullMQ worker or sync loop stops making
progress: the failures no HTTP status code describes. Works with node-cron, croner, cron, node-schedule and
BullMQ, with no hosted service and no ping URLs.

RunSignal records runs, items and heartbeats as OpenTelemetry metrics, with the same names as every other
RunSignal SDK, so the `runsignal` CLI provisions the same alerts for Node.js services as for .NET, Go, Java and
Rust ones: New Relic alerts and dashboards, or Prometheus rules and a Grafana dashboard. The
[main README](https://github.com/gokhanertogan/RunSignal#readme) covers the concepts, the alerts and the
backends.

## Installation

```bash
npm install @runsignal/node @opentelemetry/api
```

Requires Node.js 20 or later. The package is ESM only, ships its own TypeScript types, and depends only on
`@opentelemetry/api` (peer). BullMQ 5.16+ is an optional peer for the BullMQ integration.

## Quick start

```ts
import { defaultRegistry } from '@runsignal/node';

const invoices = defaultRegistry.workload('nightly-invoice', {
  schedule: '0 2 * * *',
  maxDuration: '30m',          // or milliseconds
  minProcessed: 100,
  severity: 'critical',
});

await invoices.run(async () => {
  for (const order of await unbilledOrders()) {
    await createInvoice(order);
    invoices.processed();
  }
});
```

The registry follows the global meter provider, so it works even when it is created by an import before the
OpenTelemetry SDK starts. For an explicit provider, use `new WorkloadRegistry({ meterProvider })`.

## Recording work

| Call | Records |
|---|---|
| `w.run(fn, { signal })` | One run. It succeeds when `fn` resolves, is cancelled when `signal` was aborted, and fails otherwise. The run's span is active inside `fn` (with a context manager, which `NodeSDK` registers). |
| `const run = w.start()` then `run.succeed()` / `fail(err)` / `failCode(code, msg)` / `cancel()` | A run ended by hand. Later calls are ignored. |
| `w.processed(n)` / `w.itemFailed(n)` | Items handled. |
| `w.beat()` | A heartbeat from a long-running loop. |

`error.type` is the error's `errorType` property if it has one, then its `code` if that is a string (such as
`ECONNREFUSED`), then its class name.

## Cron libraries

Since 0.2.0, `cronJob` declares a scheduled job once, with its schedule, and gives you a tick handler for any cron library.
The workload is registered immediately, so a job that never fires still gets a `missed` alert.

```ts
import { cronJob } from '@runsignal/node/cron';

const invoices = cronJob('nightly-invoice', { schedule: '0 2 * * *', maxDuration: '30m', minProcessed: 100 }, async (w) => {
  for (const order of await unbilledOrders()) {
    await createInvoice(order);
    w.processed();
  }
});

cron.schedule(invoices.schedule, invoices.run);                                 // node-cron
new Cron(invoices.schedule, invoices.run);                                      // croner
CronJob.from({ cronTime: invoices.schedule, onTick: invoices.run, start: true }); // cron
schedule.scheduleJob(invoices.schedule, invoices.run);                          // node-schedule
```

- **Failures:** a failed tick is recorded with its error type and passed to `onError` (default `console.error`). It is not rethrown, because most cron libraries turn a rejected tick into an unhandled rejection.
- **Options:** everything `workload()` takes, plus `registry` and `onError`. Declaring the same name twice throws.
- **Time zones:** if your scheduler runs the pattern in a time zone, pass the same one to the CLI with `--time-zone`.

## BullMQ

```ts
import { discoverSchedulers, instrument, workloadOf } from '@runsignal/node/bullmq';

const worker = new Worker('reports', instrument(async (job) => {
  const rows = await buildReport(job.data);
  workloadOf(job)?.processed(rows.length);
}, { workloads: { 'daily-sales-report': { maxDuration: '10m', severity: 'critical' } } }), { connection });

await queue.upsertJobScheduler('Daily Sales Report', { pattern: '0 0 6 * * *' }, { name: 'build' });
await discoverSchedulers(queue);   // registers schedulers before they first fire
```

- **What is recorded:** every job, including success, failure with its error type, cancellation (when BullMQ aborts the job) and duration. Results and errors pass through, so retries work as before.
- **Naming:** jobs from a job scheduler are named after the scheduler (`Daily Sales Report` becomes `daily-sales-report`). Other jobs are named after their queue and job name (`emails.send-welcome`).
- **Discovery:** `discoverSchedulers` takes each scheduler's cron pattern as its schedule, so a scheduler that never fires gets a `missed` alert.
- **Redis client:** in ESM, BullMQ 6 needs a constructed client, such as `connection: new Redis({ maxRetriesPerRequest: null })` from `ioredis`.

## Manifest

```ts
defaultRegistry.writeManifestFromEnv('billing-service'); // writes to RUNSIGNAL_MANIFEST_PATH when it is set
```

## Exporting

**New Relic** needs delta temporality. With cumulative, a stopped workload never looks stopped:

```ts
new OTLPMetricExporter({
  url: 'https://otlp.eu01.nr-data.net:4317',        // US: https://otlp.nr-data.net:4317
  metadata,                                         // a grpc Metadata with api-key = your license key
  temporalityPreference: AggregationTemporality.DELTA,
});
```

**Prometheus 3** receives OTLP at `/api/v1/otlp/v1/metrics` with the default (cumulative) temporality. Generate
its rules with `runsignal export prometheus`.

A complete program is in [examples/worker](https://github.com/gokhanertogan/RunSignal/tree/main/src/RunSignal.Node/examples/worker).

## Performance

Measured on Apple Silicon with Node.js 26 and the OpenTelemetry metrics SDK attached; the times include its
aggregation. Run `npm run bench`.

| Operation | Time |
|---|---|
| `start()` + `succeed()` without tracing | ~0.7 µs |
| `processed()` / `beat()` | ~0.2–0.3 µs |

With tracing, the cost depends on your span processor. For workloads that run thousands of times per second,
create the registry with `new WorkloadRegistry({ tracing: false })`.

## Development

`npm test` needs `redis-server` on the `PATH`; the BullMQ tests start their own instance.

## Links

[Specification](https://github.com/gokhanertogan/RunSignal/blob/main/spec/README.md) ·
[Changelog](https://github.com/gokhanertogan/RunSignal/blob/main/CHANGELOG.md) ·
[Other languages](https://github.com/gokhanertogan/RunSignal#quick-start)
