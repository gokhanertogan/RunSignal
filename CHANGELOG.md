# Changelog

All notable changes to RunSignal are recorded here. Every SDK and package in this repository is released together,
with the same version number. The project follows [Semantic Versioning](https://semver.org/); until 1.0, minor
versions may contain breaking changes.

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
