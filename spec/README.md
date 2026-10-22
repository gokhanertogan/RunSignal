# RunSignal signal specification (v0.1)

This is the contract every RunSignal SDK (.NET, Java, Go, Node.js, Rust) implements. Because every SDK emits
the same OpenTelemetry instruments with the same names and attributes, one set of alert rules works for every
language.

## Concepts

- **Workload**: a unit of background work that no HTTP status code describes, such as a scheduled job, a queue
  consumer, a sync loop or a batch. It is identified by a stable, low-cardinality `name`
  (`[a-z0-9][a-z0-9._-]*`, at most 128 characters), for example `nightly-invoice`.
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
| `runsignal.workload.last_success` | Gauge (int) | `s` | `runsignal.workload` | Unix time of the last successful run |

\* `error.type` is present only when `runsignal.outcome` is `failure`.

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
- SDKs must not evaluate alert rules in-process. A process that has died or hung cannot report that it is
  missing, so absence (`missed`, `stalled`) is always detected by the backend from the signals above.

## Workload expectations (used for alert provisioning)

| Field | Meaning | Alert it produces |
|---|---|---|
| `schedule` | Cron expression for when the workload should run | `missed` |
| `maxDuration` | Longest acceptable run | `overrun` |
| `expectEvery` | Longest acceptable gap between heartbeats | `stalled` |
| `minProcessed` | Fewest items a run should process | `low-output` |
| `failureRateThreshold` | Highest acceptable share of failed runs or items (0–1) | `failure-rate` |
| `severity` | `info` \| `warning` \| `critical` | Routing (for example critical → on-call) |
