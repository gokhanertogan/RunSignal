import {
  context,
  metrics,
  SpanKind,
  SpanStatusCode,
  trace,
  type Attributes as OtelAttributes,
  type Counter,
  type Histogram,
  type MeterProvider,
  type Span,
  type Tracer,
  type TracerProvider,
} from '@opentelemetry/api';
import { mkdirSync, renameSync, writeFileSync } from 'node:fs';
import { basename, dirname } from 'node:path';
import { performance } from 'node:perf_hooks';
import { toWorkloadName, validateName } from './naming.js';
import { Attributes, DURATION_BUCKETS, Metrics, SCOPE_NAME, VERSION } from './telemetry.js';

/** How urgent a workload's alerts are; it routes notifications. */
export type Severity = 'info' | 'warning' | 'critical';

/** A duration in milliseconds, or a string such as `'90s'`, `'30m'`, `'2h'` or `'1d'`. */
export type Duration = number | string;

/**
 * What a workload is expected to do. These define the alerts RunSignal provisions and do not change how signals
 * are recorded.
 */
export interface WorkloadOptions {
  /** Cron expression for when the workload should run. Produces the missed alert. */
  schedule?: string;
  /** Longest acceptable run. Produces the overrun alert. */
  maxDuration?: Duration;
  /** Longest acceptable gap between heartbeats. Produces the stalled alert. */
  expectEvery?: Duration;
  /** Fewest items a run should process. Produces the low-output alert. */
  minProcessed?: number;
  /** Highest acceptable share (0 to 1) of failed runs or items. Produces the failure-rate alert. */
  failureRateThreshold?: number;
  /** How urgent the workload's alerts are. Defaults to `'warning'`. */
  severity?: Severity;
}

/** The validated expectations of a workload, with durations in milliseconds. */
export interface Expectations {
  readonly schedule?: string;
  readonly maxDurationMs?: number;
  readonly expectEveryMs?: number;
  readonly minProcessed?: number;
  readonly failureRateThreshold?: number;
  readonly severity: Severity;
}

export interface RegistryOptions {
  /** Records metrics on this provider instead of the global one. */
  meterProvider?: MeterProvider;
  /** Records run spans on this provider instead of the global one. */
  tracerProvider?: TracerProvider;
  /** Set to false to record no span per run, for workloads that run thousands of times per second. */
  tracing?: boolean;
}

/** An error can supply its own low-cardinality `error.type`, such as `'statement_missing'`. */
export interface ErrorWithType {
  errorType: string;
}

/** Thrown when a workload's options are invalid or declared twice. */
export class WorkloadConfigurationError extends Error {
  override name = 'WorkloadConfigurationError';
}

const UNIT_MS: Record<string, number> = { s: 1000, m: 60_000, h: 3_600_000, d: 86_400_000 };

function toMs(value: Duration, option: string): number {
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || value <= 0) throw new WorkloadConfigurationError(`${option} must be a positive number of milliseconds.`);
    return value;
  }
  const match = /^(\d+(?:\.\d+)?)([smhd])$/.exec(value.trim());
  if (!match) throw new WorkloadConfigurationError(`${option} '${value}' is not a duration. Use milliseconds or a number with s, m, h or d, such as '30m'.`);
  return Number(match[1]) * UNIT_MS[match[2]!]!;
}

function toExpectations(options: WorkloadOptions): Expectations {
  const { failureRateThreshold: rate, minProcessed } = options;
  if (rate !== undefined && (rate < 0 || rate > 1)) throw new WorkloadConfigurationError(`failureRateThreshold must be between 0 and 1, got ${rate}.`);
  if (minProcessed !== undefined && (!Number.isInteger(minProcessed) || minProcessed < 0)) throw new WorkloadConfigurationError('minProcessed must be a non-negative integer.');
  const severity = options.severity ?? 'warning';
  if (!['info', 'warning', 'critical'].includes(severity)) throw new WorkloadConfigurationError(`Unknown severity '${severity}'.`);
  return {
    severity,
    ...(options.schedule !== undefined && { schedule: options.schedule }),
    ...(options.maxDuration !== undefined && { maxDurationMs: toMs(options.maxDuration, 'maxDuration') }),
    ...(options.expectEvery !== undefined && { expectEveryMs: toMs(options.expectEvery, 'expectEvery') }),
    ...(minProcessed !== undefined && { minProcessed }),
    ...(rate !== undefined && { failureRateThreshold: rate }),
  };
}

/** @internal */
export interface Instruments {
  readonly provider: MeterProvider;
  readonly runs: Counter;
  readonly duration: Histogram;
  readonly items: Counter;
  readonly heartbeats: Counter;
}

/** Creates and holds the workloads of an application and the instruments they record on. */
export class WorkloadRegistry {
  readonly #workloads = new Map<string, Workload>();
  readonly #meterProvider: MeterProvider | undefined;
  #instruments: Instruments;
  /** @internal */ readonly tracer: Tracer | undefined;

  /**
   * By default the registry records on the global OpenTelemetry providers. It follows the global meter provider
   * when it changes, so it works even when it is created, for example by an import, before the SDK starts.
   */
  constructor(options: RegistryOptions = {}) {
    this.#meterProvider = options.meterProvider;
    this.#instruments = this.#createInstruments(options.meterProvider ?? metrics.getMeterProvider());
    if (options.tracing !== false) {
      // The global tracer provider is a proxy that follows the SDK, unlike the global meter provider.
      this.tracer = (options.tracerProvider ?? trace.getTracerProvider()).getTracer(SCOPE_NAME, VERSION);
    }
  }

  /** @internal The instruments to record on, recreated if the global meter provider has changed. */
  get instruments(): Instruments {
    if (this.#meterProvider === undefined) {
      const current = metrics.getMeterProvider();
      if (current !== this.#instruments.provider) this.#instruments = this.#createInstruments(current);
    }
    return this.#instruments;
  }

  #createInstruments(provider: MeterProvider): Instruments {
    const meter = provider.getMeter(SCOPE_NAME, VERSION);
    const sinceSuccess = meter.createObservableGauge(Metrics.sinceSuccess, {
      unit: 's',
      description: 'Seconds since the last successful run of a workload, or since it was registered.',
    });
    const sinceHeartbeat = meter.createObservableGauge(Metrics.sinceHeartbeat, {
      unit: 's',
      description: 'Seconds since the last heartbeat of a workload, or since it was registered.',
    });
    meter.addBatchObservableCallback((observer) => {
      const now = performance.now();
      for (const workload of this.#workloads.values()) {
        const success = workload.secondsSinceSuccess(now);
        if (success !== undefined) observer.observe(sinceSuccess, success, workload.nameAttributes);
        const heartbeat = workload.secondsSinceHeartbeat(now);
        if (heartbeat !== undefined) observer.observe(sinceHeartbeat, heartbeat, workload.nameAttributes);
      }
    }, [sinceSuccess, sinceHeartbeat]);

    return {
      provider,
      runs: meter.createCounter(Metrics.runs, { unit: '{run}', description: 'Completed runs of a workload.' }),
      duration: meter.createHistogram(Metrics.duration, {
        unit: 's',
        description: 'Duration of workload runs.',
        advice: { explicitBucketBoundaries: [...DURATION_BUCKETS] },
      }),
      items: meter.createCounter(Metrics.items, { unit: '{item}', description: 'Items handled by workload runs.' }),
      heartbeats: meter.createCounter(Metrics.heartbeats, { unit: '{heartbeat}', description: 'Heartbeats of long-running workloads.' }),
    };
  }

  /**
   * Returns the workload called `name`, creating it with `options` on first use. Call it once and keep the
   * result. Passing options for a workload that already exists throws, so expectations are declared in exactly
   * one place.
   */
  workload(name: string, options?: WorkloadOptions): Workload {
    const existing = this.#workloads.get(name);
    if (existing) {
      if (options !== undefined) {
        throw new WorkloadConfigurationError(`Workload '${name}' is already registered; set its options only where it is first created.`);
      }
      return existing;
    }
    return this.getOrAdd(name, options);
  }

  /**
   * Returns the workload called `name`, creating it with `options` if it does not exist yet. Unlike `workload`,
   * an existing workload is returned as is: options declared elsewhere take precedence. Scheduler integrations
   * use it to name workloads after jobs.
   */
  getOrAdd(name: string, options: WorkloadOptions = {}): Workload {
    const existing = this.#workloads.get(name);
    if (existing) return existing;
    validateName(name);
    const created = new Workload(this, name, toExpectations(options));
    this.#workloads.set(name, created);
    return created;
  }

  /** Every workload created so far, sorted by name. */
  get workloads(): Workload[] {
    return [...this.#workloads.values()].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  }

  /** Describes every workload, for alert provisioning. `service` must match the OpenTelemetry `service.name`. */
  manifest(service: string): WorkloadManifest {
    return {
      version: 1,
      service,
      workloads: this.workloads.map(({ name, expectations: e }) => ({
        name,
        ...(e.schedule !== undefined && { schedule: e.schedule }),
        ...(e.maxDurationMs !== undefined && { maxDurationSeconds: e.maxDurationMs / 1000 }),
        ...(e.expectEveryMs !== undefined && { expectEverySeconds: e.expectEveryMs / 1000 }),
        ...(e.minProcessed && { minProcessed: e.minProcessed }),
        ...(e.failureRateThreshold && { failureRateThreshold: e.failureRateThreshold }),
        severity: e.severity,
      })),
    };
  }

  /** Writes the manifest as indented JSON, atomically, so a reader never sees half a manifest. */
  writeManifest(path: string, service: string): void {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(`${path}.tmp`, JSON.stringify(this.manifest(service), null, 2));
    renameSync(`${path}.tmp`, path);
  }

  /**
   * Writes the manifest if the RUNSIGNAL_MANIFEST_PATH environment variable is set, so CI can ask for one
   * without code changes. Call it once every workload has been registered. The service name is `service`, or
   * else OTEL_SERVICE_NAME, or else the script name. Returns the path written, or undefined.
   */
  writeManifestFromEnv(service?: string): string | undefined {
    const path = process.env.RUNSIGNAL_MANIFEST_PATH?.trim();
    if (!path) return undefined;
    this.writeManifest(path, service || process.env.OTEL_SERVICE_NAME || basename(process.argv[1] ?? 'node', '.js'));
    return path;
  }
}

/** A workload manifest, in the format of spec/manifest.schema.json. */
export interface WorkloadManifest {
  version: 1;
  service: string;
  workloads: Array<{
    name: string;
    schedule?: string;
    maxDurationSeconds?: number;
    expectEverySeconds?: number;
    minProcessed?: number;
    failureRateThreshold?: number;
    severity: Severity;
  }>;
}

type Outcome = 'success' | 'failure' | 'cancelled';

/**
 * A unit of background work, such as a scheduled job, a queue consumer or a sync loop. Get one from a
 * WorkloadRegistry once and keep it.
 */
export class Workload {
  /** @internal */ readonly nameAttributes: OtelAttributes;
  readonly #registry: WorkloadRegistry;
  readonly #outcomeAttributes: Record<Outcome, OtelAttributes>;
  readonly #processed: OtelAttributes;
  readonly #failed: OtelAttributes;

  // performance.now() timestamps. Until the first success or heartbeat they hold the time the workload was
  // created, so a workload that never succeeds or never beats still reports a growing age.
  #lastSuccess: number;
  #lastHeartbeat: number;
  #hasStarted = false;
  #hasBeaten = false;

  /** @internal */
  constructor(registry: WorkloadRegistry, readonly name: string, readonly expectations: Expectations) {
    this.#registry = registry;
    this.nameAttributes = Object.freeze({ [Attributes.workload]: name });
    this.#outcomeAttributes = {
      success: Object.freeze({ [Attributes.workload]: name, [Attributes.outcome]: 'success' }),
      failure: Object.freeze({ [Attributes.workload]: name, [Attributes.outcome]: 'failure' }),
      cancelled: Object.freeze({ [Attributes.workload]: name, [Attributes.outcome]: 'cancelled' }),
    };
    this.#processed = Object.freeze({ [Attributes.workload]: name, [Attributes.itemResult]: 'processed' });
    this.#failed = Object.freeze({ [Attributes.workload]: name, [Attributes.itemResult]: 'failed' });
    this.#lastSuccess = this.#lastHeartbeat = performance.now();
  }

  /**
   * Starts a run. End it with exactly one call to `succeed`, `fail`, `failCode` or `cancel`; later calls are
   * ignored. Prefer `run`, which ends the run for you and makes the run's span active for `fn`.
   */
  start(): Run {
    this.#hasStarted = true;
    const span = this.#registry.tracer?.startSpan(this.name, { kind: SpanKind.INTERNAL, attributes: this.nameAttributes });
    return new Run(this, performance.now(), span);
  }

  /**
   * Runs `fn` as one run of the workload: it succeeds when `fn` resolves, is cancelled when `fn` rejects after
   * `signal` was aborted, and fails otherwise. The result or error of `fn` is passed through.
   */
  async run<T>(fn: (signal?: AbortSignal) => T | Promise<T>, options: { signal?: AbortSignal } = {}): Promise<T> {
    const run = this.start();
    const active = run.span ? trace.setSpan(context.active(), run.span) : context.active();
    try {
      const result = await context.with(active, () => fn(options.signal));
      run.succeed();
      return result;
    } catch (error) {
      if (options.signal?.aborted) run.cancel();
      else run.fail(error);
      throw error;
    }
  }

  /** Records `count` successfully processed items. Recording 0 is useful: it reports that a run did nothing. */
  processed(count = 1): void {
    if (count >= 0) this.#registry.instruments.items.add(count, this.#processed);
  }

  /** Records `count` items that could not be processed. */
  itemFailed(count = 1): void {
    if (count >= 0) this.#registry.instruments.items.add(count, this.#failed);
  }

  /** Records a heartbeat: the workload is alive and making progress. */
  beat(): void {
    this.#lastHeartbeat = performance.now();
    this.#hasBeaten = true;
    this.#registry.instruments.heartbeats.add(1, this.nameAttributes);
  }

  /** @internal */
  complete(start: number, span: Span | undefined, outcome: Outcome, errorType?: string, error?: unknown, description?: string): void {
    const now = performance.now();
    const instruments = this.#registry.instruments;
    if (errorType === undefined) {
      instruments.runs.add(1, this.#outcomeAttributes[outcome]);
    } else {
      instruments.runs.add(1, { ...this.#outcomeAttributes.failure, [Attributes.errorType]: errorType });
    }
    instruments.duration.record((now - start) / 1000, this.#outcomeAttributes[outcome]);
    if (outcome === 'success') this.#lastSuccess = now;

    if (span) {
      if (span.isRecording()) {
        span.setAttribute(Attributes.outcome, outcome);
        if (outcome === 'failure') {
          span.setAttribute(Attributes.errorType, errorType!);
          if (error instanceof Error) span.recordException(error);
          span.setStatus({ code: SpanStatusCode.ERROR, ...(description !== undefined && { message: description }) });
        } else if (outcome === 'success') {
          span.setStatus({ code: SpanStatusCode.OK });
        }
      }
      span.end();
    }
  }

  // Reported for workloads that are scheduled or have run, so alerts can tell a missed run from a workload
  // that simply has no runs.
  /** @internal */
  secondsSinceSuccess(now: number): number | undefined {
    return this.#hasStarted || this.expectations.schedule !== undefined ? (now - this.#lastSuccess) / 1000 : undefined;
  }

  // Reported for workloads that expect heartbeats or have sent one.
  /** @internal */
  secondsSinceHeartbeat(now: number): number | undefined {
    return this.#hasBeaten || this.expectations.expectEveryMs !== undefined ? (now - this.#lastHeartbeat) / 1000 : undefined;
  }
}

/** One run of a Workload, started with `Workload.start`. */
export class Run {
  #ended = false;

  /** @internal */
  constructor(readonly workload: Workload, private readonly started: number, readonly span: Span | undefined) {}

  /** Milliseconds since the run started. */
  get elapsedMs(): number {
    return performance.now() - this.started;
  }

  /** Ends the run as successful. */
  succeed(): void {
    if (this.#end()) this.workload.complete(this.started, this.span, 'success');
  }

  /**
   * Ends the run as failed because of `error`. Its `error.type` is the error's `errorType` if it has one, then
   * its `code` if that is a string (such as Node's `'ECONNREFUSED'`), then its class name.
   */
  fail(error: unknown): void {
    if (this.#end()) this.workload.complete(this.started, this.span, 'failure', errorTypeOf(error), error, error instanceof Error ? error.message : String(error));
  }

  /**
   * Ends the run as failed without an error, for example when an expected input file is missing. `code` must be
   * a short, stable value such as `'statement_missing'`; never put messages or IDs in it.
   */
  failCode(code: string, description?: string): void {
    if (this.#end()) this.workload.complete(this.started, this.span, 'failure', code || 'unknown', undefined, description);
  }

  /** Ends the run as cancelled, for example on shutdown. Cancelled runs do not count as failures. */
  cancel(): void {
    if (this.#end()) this.workload.complete(this.started, this.span, 'cancelled');
  }

  #end(): boolean {
    if (this.#ended) return false;
    this.#ended = true;
    return true;
  }
}

function errorTypeOf(error: unknown): string {
  if (typeof error === 'object' && error !== null) {
    const { errorType, code } = error as { errorType?: unknown; code?: unknown };
    if (typeof errorType === 'string' && errorType) return errorType;
    if (typeof code === 'string' && code) return code;
    const name = error.constructor?.name;
    if (name && name !== 'Object') return name;
  }
  return typeof error;
}

/** A process-wide registry on the global OpenTelemetry providers. */
export const defaultRegistry = new WorkloadRegistry();

export { toWorkloadName };
