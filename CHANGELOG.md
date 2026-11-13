# Changelog

All notable changes to RunSignal are recorded here. Every SDK and package in this repository is released together,
with the same version number. The project follows [Semantic Versioning](https://semver.org/); until 1.0, minor
versions may contain breaking changes.

## Unreleased

### Added
- `runsignal validate`: checks manifests offline for CI. Errors are invalid names, duplicate workloads, and
  unreadable schedules or time zones. Warnings are expectations a backend cannot alert on, naming the backend.
  It exits with 1 on errors, or on warnings too with `--strict`.
- `runsignal compare`: the alerts a manifest change adds, removes or changes, offline, in reader terms such as
  "a run takes longer than 30m" → "45m". `--markdown` writes it for a pull request comment.
- GitHub Action: `pr-comment` comments on pull requests with the alert changes against the base branch and the
  validation findings, updates the same comment on every push, and fails when a manifest has errors. `command` is
  now optional, and `cli-path` uses an existing `runsignal` instead of installing one.
- Dashboards: a panel or widget for the oldest unfinished run (`active_run_age`, what `hung` alerts on) on the
  Grafana and New Relic dashboards. The New Relic workload table shows each workload's last success as a date.

### Fixed
- Grafana dashboard: per-run workloads (`runsignal exec`, `discover kubernetes`) showed no runs, items or durations,
  because each run is its own series and `increase()` cannot see it grow. Their series are now summed per run, and
  their last success comes from successful runs.

## 0.3.0 (2026-11-10)

### Added
- The CLI as a native executable of about 10 MB with no .NET dependency, for images and machines without .NET:
  - release assets for Linux (glibc 2.28+ and musl, x64 and arm64), macOS and Windows;
  - the container image `ghcr.io/gokhanertogan/runsignal` to `COPY --from` (`/runsignal`, `/runsignal-musl`).
- `runsignal exec`: runs any command (a shell script, a crontab entry, a container in a Kubernetes CronJob) as one
  run of a workload and exports it over OTLP. Exit code 0 is a success, other codes fail with `error.type`
  `exit_code_<n>`, and stop signals are forwarded and count as cancelled. The command's exit code is returned.
- `runsignal discover kubernetes`: writes a manifest from Kubernetes CronJobs, with their schedules, time zones and
  `activeDeadlineSeconds`, plus `runsignal.io/*` annotations for severity, max duration, minimum output, failure
  rate, workload name and opting out.
- Manifest: `"reporting": "per-run"` for workloads whose runs are short-lived processes. `missed` then means no
  successful run within the schedule plus grace, and there is no not-reporting or stalled alert. On New Relic,
  per-run `missed` needs a run at least every 48 hours.
- Manifest: a per-workload `timeZone` for its schedule.
- Hung runs: a new `runsignal.workload.active_run_age` gauge in every SDK reports how long the oldest unfinished
  run has been running (0 when none is). It is reported for workloads that have `maxDuration` or have started a
  run. New Relic gets a `hung` condition and Prometheus a `RunSignalHung` rule, which open when an unfinished run
  passes `maxDuration`. Before, a run that never finished only showed up later as `missed` or `stalled`. The
  Prometheus rule reads the gauge directly, so unlike `overrun` it also covers limits above the largest histogram
  bucket.
- .NET: `WorkloadRun.Discard()` forgets a started run without recording it. The Quartz integration uses it for
  vetoed jobs.

### Fixed
- `runsignal exec` no longer crashes after the command has run when it exports over HTTPS in an image without
  OpenSSL; it warns, skips the export and returns the command's exit code.
- Alert descriptions formatted durations with the machine's culture ("1,5h" on a Turkish machine, "1.5h" in CI),
  so `provision` from two machines kept updating the same conditions. They are now culture-invariant.
- Python: a second `WorkloadRegistry` on the same meter provider (for example one you create next to
  `default_registry`) silently lost its gauges, so its workloads never got `missed`, `stalled` or `hung` alerts.
  Registries on one provider now share their gauges.
- Node.js: a workload in `defaultRegistry` that never runs reports its gauges once the SDK has started, so it gets
  its `missed` alert. Before, the gauges moved to the SDK's meter provider only on the first recording.
- Java: a registered workload reports its gauges at startup, so a cron job that never fires gets its `missed`
  alert. `WorkloadRegistry.global()` does this only once `GlobalOpenTelemetry` is set (as the Java agent does), so
  it never fixes the global instance to a no-op.
- Java (Spring Batch 6): when the final job repository update fails, Spring Batch skips `afterJob`, which left the
  run in progress. A background sweep now ends such runs as failed with `error.type`
  `job_repository_update_failed`, about 30 to 60 seconds after the job ends.
- Java: a reactive `@Scheduled` method whose subscription is cancelled is recorded as cancelled, not successful.
- Go: a run whose function calls `runtime.Goexit` (as `t.FailNow` does) fails with `error.type` `goexit` instead
  of staying in progress.
- Rust: a registered workload reports its gauges straight away, so a scheduled workload that never runs gets its
  `missed` alert. Before, gauges appeared only after the first recording. `Registry::global()` follows the global
  meter provider until it first collects. If workloads are registered before the provider is set and record
  nothing, call the new `Registry::refresh_meter_provider()` once after setting it.
- Python (Celery): a task id started twice (a redelivery, or a nested eager apply) no longer loses its first run.
  `instrument()` on a second registry now connects its signal handlers and records on that registry, instead of
  silently doing nothing or recording on the first one.

### Changed
- A run that is started and never ended now stays in progress, so it eventually opens `hung`. Before, it recorded
  nothing.

## 0.2.0 (2026-10-29)

### Added
- Python: the `runsignal` package on PyPI, with Celery (tasks and beat schedules) and APScheduler 3
  integrations, `with` / `async with` runs and a `@monitor` decorator. It passes the shared conformance vectors
  and writes the same manifest as the other SDKs.
- Node.js: `cronJob` (`@runsignal/node/cron`) monitors jobs scheduled with node-cron, croner, cron or
  node-schedule, and registers them with their schedule at startup so a job that never fires gets a `missed`
  alert.
- CLI: `runsignal export grafana` writes a Grafana dashboard for RunSignal metrics in Prometheus.
- NuGet packages have an icon.
- A GitHub Action (`uses: gokhanertogan/RunSignal@<ref>`) that installs the CLI and runs any of its commands in CI.

## 0.1.0 (2026-10-24)

First release.

### Signals and specification
- The same OpenTelemetry metrics in every SDK: runs, duration, items, heartbeats, and the `since_success` and
  `since_heartbeat` gauges. Runs are also recorded as spans.
- A workload manifest format (`spec/manifest.schema.json`) and cross-language conformance vectors for naming
  and signals.

### SDKs and integrations
- .NET: `RunSignal.Core`, `RunSignal.DotNet`, `RunSignal.OpenTelemetry`, plus Hangfire 1.8+ (`RunSignal.Hangfire`)
  and Quartz.NET 3 and 4 (`RunSignal.Quartz`, `RunSignal.Quartz4`).
- Go: `github.com/gokhanertogan/RunSignal/go`, with robfig/cron (`rscron`).
- Java: `io.github.gokhanertogan.runsignal:runsignal` and `runsignal-spring-boot-starter` for Spring
  `@Scheduled` and Spring Batch 5 and 6.
- Node.js: `@runsignal/node`, with BullMQ.
- Rust: `runsignal`.

### Alerting
- `runsignal` CLI: `plan`, `diff`, `provision` and `destroy` for New Relic alert policies, NRQL conditions,
  workflows and dashboards, and `export prometheus` for Prometheus alerting rules.
- Alerts: missed, failed, stalled, overrun, low output, failure rate, item failure rate and not reporting.
