// Runs two workloads that HTTP monitoring never sees: a BullMQ job scheduler that builds a report every minute
// and a stock-sync loop. With NEW_RELIC_LICENSE_KEY set, signals go to New Relic over OTLP
// (NEW_RELIC_REGION=EU for the EU data center); otherwise metrics are printed to the console.
// Needs Redis on localhost:6379. Run: RUNSIGNAL_MANIFEST_PATH=runsignal.json npm start
import { Metadata } from '@grpc/grpc-js';
import { metrics } from '@opentelemetry/api';
import { OTLPMetricExporter } from '@opentelemetry/exporter-metrics-otlp-grpc';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { AggregationTemporality, ConsoleMetricExporter, MeterProvider, PeriodicExportingMetricReader } from '@opentelemetry/sdk-metrics';
import { defaultRegistry } from '@runsignal/node';
import { discoverSchedulers, instrument, workloadOf } from '@runsignal/node/bullmq';
import { Queue, Worker } from 'bullmq';
import { Redis } from 'ioredis';

const service = 'runsignal-node-sample';
const licenseKey = process.env.NEW_RELIC_LICENSE_KEY;

// Delta temporality is required: with cumulative, a stopped workload never looks stopped.
let exporter = new ConsoleMetricExporter();
if (licenseKey) {
  const metadata = new Metadata();
  metadata.set('api-key', licenseKey);
  exporter = new OTLPMetricExporter({
    url: process.env.NEW_RELIC_REGION?.toUpperCase() === 'EU' ? 'https://otlp.eu01.nr-data.net:4317' : 'https://otlp.nr-data.net:4317',
    metadata,
    temporalityPreference: AggregationTemporality.DELTA,
  });
}
const meterProvider = new MeterProvider({
  resource: resourceFromAttributes({ 'service.name': service }),
  readers: [new PeriodicExportingMetricReader({ exporter, exportIntervalMillis: 30_000 })],
});
metrics.setGlobalMeterProvider(meterProvider);

const connection = () => new Redis({ maxRetriesPerRequest: null });
const queue = new Queue('reports', { connection: connection() });
await queue.upsertJobScheduler('Daily Sales Report', { pattern: '0 * * * * *' }, { name: 'build' });

const worker = new Worker('reports', instrument(async (job) => {
  await new Promise((r) => setTimeout(r, 100 + Math.random() * 700));
  if (Math.random() < 0.2) throw Object.assign(new Error('warehouse API did not answer'), { code: 'EWAREHOUSE' });
  workloadOf(job)?.processed(1 + Math.floor(Math.random() * 20));
}, { workloads: { 'daily-sales-report': { maxDuration: '30s', severity: 'critical' } } }), { connection: connection() });

await discoverSchedulers(queue, { workloads: { 'daily-sales-report': { maxDuration: '30s', severity: 'critical' } } });
const stock = defaultRegistry.workload('stock-sync', { expectEvery: '5m', failureRateThreshold: 0.2 });
const written = defaultRegistry.writeManifestFromEnv(service);
if (written) console.log(`wrote the RunSignal manifest to ${written}`);

const loop = setInterval(() => {
  stock.processed(Math.floor(Math.random() * 50));
  if (Math.random() < 0.1) stock.itemFailed();
  stock.beat();
}, 2_000);

process.on('SIGINT', async () => {
  clearInterval(loop);
  await worker.close();
  await queue.close();
  await meterProvider.shutdown();
  process.exit(0);
});
