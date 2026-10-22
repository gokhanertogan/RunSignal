import { context } from '@opentelemetry/api';
import { AsyncLocalStorageContextManager } from '@opentelemetry/context-async-hooks';
import {
  AggregationTemporality,
  DataPointType,
  MeterProvider,
  MetricReader,
} from '@opentelemetry/sdk-metrics';
import { BasicTracerProvider, InMemorySpanExporter, SimpleSpanProcessor } from '@opentelemetry/sdk-trace-base';
import { WorkloadRegistry, type RegistryOptions } from '../src/index.js';

// What NodeSDK registers in an application: carries the active span across awaits.
context.setGlobalContextManager(new AsyncLocalStorageContextManager().enable());

class TestReader extends MetricReader {
  constructor() {
    super({ aggregationTemporalitySelector: () => AggregationTemporality.DELTA });
  }
  protected override async onForceFlush(): Promise<void> {}
  protected override async onShutdown(): Promise<void> {}
}

export interface Point {
  value: number;
  attributes: Record<string, unknown>;
}

/** A reader for a provider the test registers globally. */
export function globalReader() {
  const reader = new TestReader();
  return { reader, collect: () => collectFrom(reader) };
}

async function collectFrom(reader: MetricReader): Promise<Record<string, Point[]>> {
  const { resourceMetrics } = await reader.collect();
  const out: Record<string, Point[]> = {};
  for (const scope of resourceMetrics.scopeMetrics) {
    for (const metric of scope.metrics) {
      for (const dp of metric.dataPoints) {
        const value = metric.dataPointType === DataPointType.HISTOGRAM ? (dp.value as { sum: number }).sum : (dp.value as number);
        (out[metric.descriptor.name] ??= []).push({ value, attributes: dp.attributes });
      }
    }
  }
  return out;
}

/** A registry wired to in-memory OpenTelemetry providers. */
export function harness(options: Pick<RegistryOptions, 'tracing'> = {}) {
  const reader = new TestReader();
  const spans = new InMemorySpanExporter();
  const registry = new WorkloadRegistry({
    ...options,
    meterProvider: new MeterProvider({ readers: [reader] }),
    tracerProvider: new BasicTracerProvider({ spanProcessors: [new SimpleSpanProcessor(spans)] }),
  });

  /** Every data point since the last collection, per metric name. */
  const collect = () => collectFrom(reader);

  return { registry, spans, collect };
}
