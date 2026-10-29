import { defaultRegistry, type Workload, type WorkloadOptions, type WorkloadRegistry } from './registry.js';

export interface CronJobOptions extends WorkloadOptions {
  /** Cron expression the job runs on. Pass `job.schedule` to your scheduler so the alert and the job agree. */
  schedule: string;
  /** The registry to record on. Defaults to `defaultRegistry`. */
  registry?: WorkloadRegistry;
  /**
   * Called with the error of a failed run, after the failure is recorded. Defaults to `console.error`. The error
   * is not rethrown, because most cron libraries turn a rejected tick into an unhandled rejection.
   */
  onError?: (error: unknown, workload: Workload) => void;
}

export interface CronJob {
  /** The workload each tick runs as, for counting items or heartbeats outside `fn`. */
  readonly workload: Workload;
  /** The cron expression the job was declared with. */
  readonly schedule: string;
  /** Runs `fn` once as a run of the workload. Pass it to your cron library as the tick handler. */
  readonly run: () => Promise<void>;
}

/**
 * Declares a scheduled job as a workload with `options.schedule` as its schedule, so it gets a missed alert from
 * startup, even before it first fires. Works with any cron library: node-cron, croner, cron or node-schedule.
 *
 * ```ts
 * const invoices = cronJob('nightly-invoice', { schedule: '0 2 * * *', maxDuration: '30m' }, async (w) => {
 *   for (const order of await unbilledOrders()) { await createInvoice(order); w.processed(); }
 * });
 * cron.schedule(invoices.schedule, invoices.run);   // node-cron
 * new Cron(invoices.schedule, invoices.run);        // croner
 * ```
 */
export function cronJob(name: string, options: CronJobOptions, fn: (workload: Workload) => unknown): CronJob {
  const { registry = defaultRegistry, onError = defaultOnError, ...expectations } = options;
  const workload = registry.workload(name, expectations);
  return {
    workload,
    schedule: options.schedule,
    run: async () => {
      try {
        await workload.run(() => fn(workload));
      } catch (error) {
        onError(error, workload);
      }
    },
  };
}

function defaultOnError(error: unknown, workload: Workload): void {
  console.error(`runsignal: a run of ${workload.name} failed`, error);
}
