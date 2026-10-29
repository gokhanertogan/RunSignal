# RunSignal for Python

[![PyPI](https://img.shields.io/pypi/v/runsignal)](https://pypi.org/project/runsignal/)
[![Python versions](https://img.shields.io/pypi/pyversions/runsignal)](https://pypi.org/project/runsignal/)
[![license](https://img.shields.io/pypi/l/runsignal)](https://github.com/gokhanertogan/RunSignal/blob/main/LICENSE)

**Cron job and background job monitoring for Python, on OpenTelemetry.** Get an alert when a Celery task or
Celery beat schedule, an APScheduler job or a cron script is missed, fails, runs too long or processes nothing,
and when a consumer or worker loop stops making progress: the failures no HTTP status code describes. No hosted
service and no ping URLs: the alerts live in the backend you already use.

RunSignal records runs, items and heartbeats as OpenTelemetry metrics, with the same names as every other
RunSignal SDK, so the `runsignal` CLI provisions the same alerts for Python services as for .NET, Go, Java,
Node.js and Rust ones: New Relic alerts and dashboards, or Prometheus rules and a Grafana dashboard. The
[main README](https://github.com/gokhanertogan/RunSignal#readme) covers the concepts, the alerts and the
backends.

## Installation

```bash
pip install runsignal                # core, depends only on opentelemetry-api
pip install "runsignal[celery]"      # with Celery 5.3+
pip install "runsignal[apscheduler]" # with APScheduler 3.10+
```

Requires Python 3.9 or later. Install and configure the OpenTelemetry SDK and an exporter as usual
(`opentelemetry-sdk`, `opentelemetry-exporter-otlp`).

## Quick start

```python
from runsignal import default_registry

invoices = default_registry.workload(
    "nightly-invoice",
    schedule="0 2 * * *",
    max_duration="30m",      # or seconds, or a timedelta
    min_processed=100,
    severity="critical",
)

with invoices.run():
    for order in unbilled_orders():
        create_invoice(order)
        invoices.processed()
```

Or as a decorator, for sync and async functions:

```python
from runsignal import monitor

@monitor("nightly-invoice", schedule="0 2 * * *", max_duration="30m")
def generate_invoices():
    for order in unbilled_orders():
        create_invoice(order)
        generate_invoices.workload.processed()
```

`default_registry` records on the global OpenTelemetry providers, which forward to the SDK once it is configured,
so it works even when it is created by an import first. For explicit providers, use
`WorkloadRegistry(meter_provider=..., tracer_provider=...)`.

## Recording work

| Call | Records |
|---|---|
| `with w.run():` / `async with w.run():` | One run. It succeeds when the block completes, is cancelled on `asyncio.CancelledError`, `KeyboardInterrupt` or `SystemExit`, and fails on any other exception, which propagates. The run's span is current inside the block. |
| `@w.monitor` / `@monitor(name, **expectations)` | One run per call, for sync and async functions. |
| `run = w.start()` then `run.succeed()` / `fail(exc)` / `fail_code(code, description)` / `cancel()` | A run ended by hand. Later calls are ignored. |
| `w.processed(n)` / `w.item_failed(n)` | Items handled. |
| `w.beat()` | A heartbeat from a long-running loop. |

`error.type` is the exception's `error_type` attribute if it has one (such as `"statement_missing"`), otherwise
its fully qualified class name (`ValueError`, `requests.exceptions.ConnectionError`).

## Celery

```python
from celery import Celery
from celery.schedules import crontab
from runsignal.celery import instrument, workload_of

app = Celery("billing")
app.conf.beat_schedule = {
    "nightly-invoice": {"task": "billing.tasks.generate_invoices", "schedule": crontab(hour=2, minute=0)},
}
instrument(app, workloads={"billing.tasks.generate_invoices": {"max_duration": "30m", "severity": "critical"}})

@app.task(bind=True)
def generate_invoices(self):
    for order in unbilled_orders():
        create_invoice(order)
        workload_of(self).processed()
```

- **What is recorded:** every task execution, with no code in your tasks: success, failure with the exception's type (each retry counts as a failed run), cancellation when a task is revoked, and duration. Celery's own `celery.*` tasks are skipped.
- **Naming:** tasks are named after their task name (`billing.tasks.generate_invoices`).
- **Celery beat:** `instrument(app)` registers every task in `beat_schedule` with its `crontab` as the schedule, so a periodic task that never runs gets a `missed` alert. Interval schedules get no schedule.
- **Manifest:** the worker writes it when it is ready if `RUNSIGNAL_MANIFEST_PATH` is set. In CI, import the app and call `default_registry.write_manifest_from_env("billing")` instead; no broker is needed.
- **Prefork workers:** configure the OpenTelemetry SDK in `worker_process_init`, as for any OpenTelemetry instrumentation of Celery.

## APScheduler

```python
from apscheduler.schedulers.background import BackgroundScheduler
from runsignal.apscheduler import instrument

scheduler = BackgroundScheduler()
scheduler.add_job(generate_invoices, "cron", hour=2, minute=0, id="nightly-invoice")
instrument(scheduler, workloads={"nightly-invoice": {"max_duration": "30m", "severity": "critical"}})
scheduler.start()
```

- **What is recorded:** every run of every job, including jobs added after `instrument`, for all schedulers (`BackgroundScheduler`, `BlockingScheduler`, `AsyncIOScheduler`, ...).
- **Naming:** jobs are named after the `id` you gave them, or their `name` when APScheduler generated the id.
- **Schedules:** cron triggers become the workload's schedule, with APScheduler's Monday = 0 converted to cron's numbering. Triggers that use `year`, `week` or days such as `1st mon` get no schedule, with a warning.
- **Persistent job stores** (SQLAlchemy, Redis, MongoDB) cannot store a wrapped function: decorate the job function with `@default_registry.monitor("nightly-invoice")` instead. `instrument` still registers the schedule.

## Cron scripts and loops

A script run by cron, a Kubernetes CronJob or systemd timer is one run per invocation:

```python
with default_registry.workload("nightly-invoice", schedule="0 2 * * *").run():
    main()
```

The `missed` alert needs a long-running process to report `since_success`, so for short-lived scripts declare
the workload in the service that exports continuously, or rely on the `failed`, `overrun` and `low output`
alerts. Long-running loops send heartbeats:

```python
sync = default_registry.workload("stock-sync", expect_every="5m")
while True:
    sync_stock()
    sync.beat()
```

## Manifest

```python
default_registry.write_manifest_from_env("billing-service")  # writes to RUNSIGNAL_MANIFEST_PATH when it is set
```

Then provision the alerts from CI with the [`runsignal` CLI](https://github.com/gokhanertogan/RunSignal#cli).

## Exporting

**New Relic** needs delta temporality. With cumulative, a stopped workload never looks stopped:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.eu01.nr-data.net:4317   # US: https://otlp.nr-data.net:4317
export OTEL_EXPORTER_OTLP_HEADERS=api-key=<your license key>
export OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta
```

**Prometheus 3** receives OTLP at `/api/v1/otlp/v1/metrics` with the default (cumulative) temporality. Generate
its rules with `runsignal export prometheus` and a dashboard with `runsignal export grafana`.

## Development

```bash
python -m venv .venv && .venv/bin/pip install -e ".[celery,apscheduler]" opentelemetry-sdk pytest
.venv/bin/pytest
```

The tests include the cross-language conformance vectors in `spec/conformance`.

## Links

[Specification](https://github.com/gokhanertogan/RunSignal/blob/main/spec/README.md) ·
[Changelog](https://github.com/gokhanertogan/RunSignal/blob/main/CHANGELOG.md) ·
[Other languages](https://github.com/gokhanertogan/RunSignal#quick-start)
