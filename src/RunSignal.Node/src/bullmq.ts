import type { Job, Processor, Queue } from 'bullmq';
import { toWorkloadName } from './naming.js';
import { defaultRegistry, type Workload, type WorkloadOptions, type WorkloadRegistry } from './registry.js';

export interface InstrumentOptions {
  /** The registry to record on. Defaults to `defaultRegistry`. */
  registry?: WorkloadRegistry;
  /** Expectations per workload name, applied when the workload is first created. */
  workloads?: Record<string, WorkloadOptions>;
}

/**
 * The workload a job runs as: its job scheduler ID for jobs created by `upsertJobScheduler` (so repeatable jobs
 * keep one name across runs), otherwise the queue and job name, such as `emails.send-welcome`.
 */
export function workloadNameOf(job: Pick<Job, 'name' | 'queueName' | 'repeatJobKey'>): string {
  return toWorkloadName(job.repeatJobKey ?? `${job.queueName}.${job.name}`);
}

/**
 * Wraps a BullMQ processor so every job is recorded as a run of its workload: success, failure with the job's
 * error type, cancellation (when BullMQ aborts the job) and duration. The processor's result and errors pass
 * through unchanged, so retries and failed-job handling work as before. Count items with
 * `workloadOf(job)?.processed()`.
 *
 * ```ts
 * new Worker('emails', instrument(async (job) => send(job.data)), { connection });
 * ```
 */
export function instrument<T, R, N extends string>(processor: Processor<T, R, N>, options: InstrumentOptions = {}): Processor<T, R, N> {
  const registry = options.registry ?? defaultRegistry;
  const cache = new Map<string, Workload>();

  return (job, token, signal) => {
    const name = workloadNameOf(job);
    let workload = cache.get(name);
    if (!workload) {
      workload = registry.getOrAdd(name, options.workloads?.[name]);
      cache.set(name, workload);
    }
    jobWorkloads.set(job, workload);
    return workload.run(() => processor(job, token, signal), signal === undefined ? {} : { signal });
  };
}

const jobWorkloads = new WeakMap<object, Workload>();

/** The workload the job runs as inside an `instrument`ed processor, for counting items. */
export function workloadOf(job: object): Workload | undefined {
  return jobWorkloads.get(job);
}

/**
 * Registers a workload for every job scheduler of `queue`, with its cron pattern as the schedule, so a
 * repeatable job that never fires is still alerted on. Call it after `upsertJobScheduler` and before writing
 * the manifest. Schedulers that use `every` instead of a cron pattern get no schedule. Time zones are not
 * carried over: pass `--time-zone` to the CLI if your patterns use one.
 */
export async function discoverSchedulers(queue: Pick<Queue, 'getJobSchedulers'>, options: InstrumentOptions = {}): Promise<Workload[]> {
  const registry = options.registry ?? defaultRegistry;
  const found: Workload[] = [];
  for (const scheduler of await queue.getJobSchedulers()) {
    const name = toWorkloadName(scheduler.key);
    found.push(registry.getOrAdd(name, {
      ...(scheduler.pattern && { schedule: scheduler.pattern }),
      ...options.workloads?.[name],
    }));
  }
  return found;
}
