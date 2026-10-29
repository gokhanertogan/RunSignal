# RunSignal signal specification (v0.1)

This is the contract every RunSignal SDK (.NET, Java, Go, Node.js, Python, Rust) implements. Because every SDK emits
the same OpenTelemetry instruments with the same names and attributes, one set of alert rules works for every
language.

## Concepts

- **Workload**: a unit of background work that no HTTP status code describes, such as a scheduled job, a queue
  consumer, a sync loop or a batch. It is identified by a stable, low-cardinality `name`
  (`[a-z0-9][a-z0-9._-]*`, at most 128 characters), for example `nightly-invoice`. SDKs that name workloads
  after jobs derive the name the same way everywhere: strip diacritics, split camel case with `-`, lowercase,
  replace other characters with `-`, and keep `.` and `_` (`InvoiceJobs.GenerateInvoices` becomes
  `invoice-jobs.generate-invoices`).
- **Run**: one execution of a workload. It ends with exactly one outcome.
- **Item**: one unit of work inside a run, such as an invoice, a message or a row.
- **Heartbeat**: a periodic "I am still alive and making progress" mark from a long-running loop.

## Instrumentation scope

| | Name |
|---|---|
| Meter | `RunSignal` |
| Tracer / ActivitySource | `RunSignal` |

## Metrics

| Instrument | Type | Unit | Attributes | Meaning |
|---|---|---|---|---|
| `runsignal.workload.runs` | Counter (int) | `{run}` | `runsignal.workload`, `runsignal.outcome`, `error.type`* | Completed runs |
| `runsignal.workload.duration` | Histogram (double) | `s` | `runsignal.workload`, `runsignal.outcome` | Run duration |
| `runsignal.workload.items` | Counter (int) | `{item}` | `runsignal.workload`, `runsignal.item.result` | Items handled |
| `runsignal.workload.heartbeats` | Counter (int) | `{heartbeat}` | `runsignal.workload` | Heartbeats |
| `runsignal.workload.since_success` | Gauge (double) | `s` | `runsignal.workload` | Seconds since the last successful run** |
| `runsignal.workload.since_heartbeat` | Gauge (double) | `s` | `runsignal.workload` | Seconds since the last heartbeat*** |

\* `error.type` is present only when `runsignal.outcome` is `failure`.

\*\* Until the first success, counted from when the workload was registered. Reported for workloads that have a
`schedule` or have started a run.

\*\*\* Until the first heartbeat, counted from when the workload was registered. Reported for workloads that have
`expectEvery` or have sent a heartbeat.

The two gauges exist because backends can only notice that a signal stopped if it reported at least once
(New Relic's loss-of-signal, for example). A job that never runs never reports a run. Its `since_success`
keeps growing from registration instead, so a plain threshold catches it. Gauges are reported on every export
while the process is alive, so loss of signal on them means the process itself stopped reporting.

### Attribute values

- `runsignal.outcome`: `success` | `failure` | `cancelled`. A cancelled run (for example on shutdown) must not
  count as a failure.
- `runsignal.item.result`: `processed` | `failed`.
- `error.type`: the exception's fully qualified type name, or a short, stable code the caller supplies
  (for example `statement_missing`). It must stay low-cardinality: never put messages or IDs in it.

### Duration buckets

Background work runs from milliseconds to hours, so SDKs should configure these explicit bucket boundaries
(in seconds) for `runsignal.workload.duration` instead of the OpenTelemetry defaults, which stop at 10 s:

```
0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800
```

## Spans

Each run may be recorded as an `INTERNAL` span named after the workload, with the attribute
`runsignal.workload`. A failed run sets the span status to `ERROR`, sets `error.type`, and records an
`exception` event when an exception is available.

## Performance requirements

- Recording a run, an item or a heartbeat must not allocate on the heap when no span is being recorded.
- SDKs must not evaluate alert rules in-process. A process that has died cannot report that it is missing, so
  the backend detects absence: `missed` and `stalled` from the `since_*` gauges, and a dead process from loss
  of signal on those gauges.
- Counters must be exported with **delta** temporality. With cumulative temporality the last value is
  re-exported on every interval, so a backend never sees a signal stop.

## Workload expectations (used for alert provisioning)

| Field | Meaning | Alert it produces |
|---|---|---|
| `schedule` | Cron expression for when the workload should run: 5-field Unix cron, or 6 fields with seconds first. Day of week is 0-7 with 0 and 7 = Sunday, as in Unix cron. SDKs convert scheduler-specific formats, such as Quartz's 1 = Sunday, before writing it | `missed` |
| `maxDuration` | Longest acceptable run | `overrun` |
| `expectEvery` | Longest acceptable gap between heartbeats | `stalled` |
| `minProcessed` | Fewest items a run should process | `low-output` |
| `failureRateThreshold` | Highest acceptable share of failed runs or items (0–1) | `failure-rate` |
| `severity` | `info` \| `warning` \| `critical` | Routing (for example critical → on-call) |

## Workload manifest

A manifest lists a service's workloads and their expectations. It is the input for alert provisioning. Its
format is defined by [manifest.schema.json](manifest.schema.json):

```json
{
  "version": 1,
  "service": "billing-service",
  "workloads": [
    { "name": "nightly-invoice", "schedule": "0 2 * * *", "maxDurationSeconds": 1800, "minProcessed": 100, "severity": "critical" },
    { "name": "stock-sync", "expectEverySeconds": 300, "failureRateThreshold": 0.2, "severity": "warning" }
  ]
}
```

SDKs produce the manifest from the running application, not at build time. Schedules often exist only at
runtime (for example Hangfire recurring jobs registered in code, or cron triggers loaded from configuration), so
only the running application knows its complete set of workloads. At startup, SDKs discover jobs that have not
run yet from their schedulers, so a job that never starts still gets a `missed` alert. Durations are in seconds.
Workloads are sorted by name, and the manifest contains no timestamps, so it diffs cleanly.

## Conformance

[conformance/](conformance) holds the vectors every SDK tests against:

- [workload-names.json](conformance/workload-names.json): how job identifiers become workload names.
- [signals.json](conformance/signals.json): a scenario of runs, items and heartbeats, and the exact metrics it must produce (names, types, units, attributes, values and histogram bounds).
