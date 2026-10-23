# RunSignal for Node.js

Monitor Node.js background work that no HTTP status code describes, such as BullMQ queues, job schedulers and
sync loops, with OpenTelemetry. It emits the same signals and manifest as every other RunSignal SDK, so the
`runsignal` CLI provisions the same New Relic alerts for Node.js services as for .NET and Go ones. See the
[main README](../../README.md) for the concepts and the alerts.

```bash
npm install @runsignal/node @opentelemetry/api   # once published; until then, build from source
```

Requires Node.js 20 or later. The package is ESM only and ships its own TypeScript types.

## Usage

```ts
import { defaultRegistry } from '@runsignal/node';

const invoices = defaultRegistry.workload('nightly-invoice', {
  schedule: '0 2 * * *',
  maxDuration: '30m',          // or milliseconds
  minProcessed: 100,
  severity: 'critical',
});

// One run: succeeds when fn resolves, is cancelled if the signal was aborted, fails otherwise.
await invoices.run(async (signal) => {
  for (const order of await unbilledOrders()) {
    await createInvoice(order, { signal });
    invoices.processed();
  }
}, { signal: shutdown.signal });
```

A long-running loop sends heartbeats, so a loop that is alive but stuck is noticed:

```ts
const sync = defaultRegistry.workload('stock-sync', { expectEvery: '5m' });
setInterval(async () => {
  await syncStock();
  sync.beat();
}, 60_000);
```

Manual runs, and failures without an error:

```ts
const run = imports.start();
const file = await fetchStatement();
if (!file) {
  run.failCode('statement_missing', 'no bank statement today');
} else {
  run.succeed();
}
```

`error.type` is the error's `errorType` property if it has one, then its `code` if that is a string (such as
`ECONNREFUSED`), then its class name.

The registry follows the global meter provider. It works even when it is created, for example by an import,
before the OpenTelemetry SDK starts. Run spans are nested under the active span when a context manager is
registered, which `NodeSDK` does for you.

### BullMQ

```ts
import { discoverSchedulers, instrument, workloadOf } from '@runsignal/node/bullmq';

const worker = new Worker('reports', instrument(async (job) => {
  const rows = await buildReport(job.data);
  workloadOf(job)?.processed(rows.length);
}, { workloads: { 'daily-sales-report': { maxDuration: '10m', severity: 'critical' } } }), { connection });

await queue.upsertJobScheduler('Daily Sales Report', { pattern: '0 0 6 * * *' }, { name: 'build' });
await discoverSchedulers(queue);   // registers schedulers before they first fire
```

- **What gets recorded:** every job, including success, failure with its error type, cancellation (when BullMQ aborts the job) and duration. Results and errors pass through, so retries work as before.
- **Naming:** jobs from a job scheduler are named after the scheduler (`Daily Sales Report` becomes `daily-sales-report`). Other jobs are named after their queue and job name (`emails.send-welcome`).
- **Discovery:** `discoverSchedulers` takes each scheduler's cron pattern as its schedule, so a scheduler that never fires gets a `missed` alert. Schedulers that use `every` get no schedule.
- **Redis client:** in ESM, BullMQ 6 needs a constructed Redis client, such as `connection: new Redis({ maxRetriesPerRequest: null })` from `ioredis`, instead of connection options.

### Manifest

```ts
defaultRegistry.writeManifestFromEnv('billing-service'); // writes to RUNSIGNAL_MANIFEST_PATH when it is set
```

### Exporting to New Relic

Use delta temporality. With cumulative, a stopped workload never looks stopped:

```ts
new OTLPMetricExporter({
  url: 'https://otlp.eu01.nr-data.net:4317',        // US: https://otlp.nr-data.net:4317
  metadata,                                         // a grpc Metadata with api-key = your license key
  temporalityPreference: AggregationTemporality.DELTA,
});
```

A complete program is in [examples/worker](examples/worker).

## Performance

Measured on Apple Silicon with Node.js 26 and the OpenTelemetry metrics SDK attached; the times include its
aggregation. Run with `npm run bench`.

| Operation | Time |
|---|---|
| `start()` + `succeed()` without tracing | ~0.7 µs |
| `processed()` / `beat()` | ~0.2–0.3 µs |

With tracing, the cost depends on your span processor. For workloads that run thousands of times per second,
create the registry with `new WorkloadRegistry({ tracing: false })`.

## Development

```bash
npm test      # needs redis-server on the PATH; the BullMQ tests start their own instance
```
