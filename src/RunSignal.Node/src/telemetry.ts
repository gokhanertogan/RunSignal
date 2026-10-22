/**
 * Names of the meter, tracer, instruments and attributes RunSignal emits. They are identical in every RunSignal
 * SDK (see spec/README.md in the repository), so one set of alert rules covers every language.
 */
export const SCOPE_NAME = 'RunSignal';
export const VERSION = '0.1.0';

export const Metrics = {
  runs: 'runsignal.workload.runs',
  duration: 'runsignal.workload.duration',
  items: 'runsignal.workload.items',
  heartbeats: 'runsignal.workload.heartbeats',
  sinceSuccess: 'runsignal.workload.since_success',
  sinceHeartbeat: 'runsignal.workload.since_heartbeat',
} as const;

export const Attributes = {
  workload: 'runsignal.workload',
  outcome: 'runsignal.outcome',
  itemResult: 'runsignal.item.result',
  errorType: 'error.type',
} as const;

/**
 * Histogram bucket boundaries, in seconds, for the duration metric: background work runs from milliseconds to
 * hours, while the OpenTelemetry defaults stop at 10 seconds.
 */
export const DURATION_BUCKETS: readonly number[] = [
  0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800,
];
