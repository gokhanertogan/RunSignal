# Roadmap

RunSignal's goal is to make background work that no HTTP status code describes as easy to alert on as an
HTTP endpoint, in every major backend language. The plan favours two things throughout: **ergonomics** (one
line to set up, monitoring without hand-written ping code) and **performance** (no allocations on the hot
path, no in-process rule evaluation).

## Principles

**Ergonomics**
- One-line setup: `AddRunSignal()` plus `AddRunSignalInstrumentation()`.
- Zero-code monitoring where a scheduler exists (Hangfire, Quartz, BullMQ, Spring `@Scheduled`).
- `RunAsync` ends runs correctly for you: success, failure and cancellation on shutdown.
- The same concepts and names in every language, so moving between SDKs needs no relearning.
- Fail fast on mistakes: invalid names, conflicting configuration and out-of-range values throw at startup.

**Performance**
- Recording a run, an item or a heartbeat allocates nothing. Workloads are cached handles, runs are structs, and tags are prebuilt.
- Signals use the platform's native instruments (`System.Diagnostics.Metrics`, OpenTelemetry APIs), which cost almost nothing when nobody listens.
- Bounded cardinality: attributes are only the workload name, outcome and a low-cardinality error type.
- No in-process alert evaluation: a dead process cannot report itself missing, so the backend evaluates every rule.
- BenchmarkDotNet results are tracked in the README, and a regression in allocations counts as a bug.

## Phases

### Phase 0: Foundation ✅
- Monorepo, MIT license, cross-language signal spec ([spec/README.md](../spec/README.md)).

### Phase 1: .NET core ✅
- `RunSignal.Core`: `WorkloadRegistry`, `Workload`, `WorkloadRun`, `WorkloadOptions`; five instruments plus run spans.
- `RunSignal.DotNet`: `AddRunSignal().AddWorkload(...)`, keyed injection, eager workload creation.
- `RunSignal.OpenTelemetry`: `AddRunSignalInstrumentation()` for metrics and traces, with duration buckets for jobs.
- Tests on net8.0 and net10.0, benchmarks, and a sample worker.

### Phase 2: Scheduler integrations (.NET)
- `RunSignal.Hangfire`: a server filter that records every job run automatically; a `[Workload]` attribute; schedules read from recurring-job cron expressions.
- `RunSignal.Quartz`: an `IJobListener` that does the same, with schedules read from triggers.
- Item counting from job code through an injected context.

### Phase 3: Workload manifest
- A JSON manifest of every workload and its expectations, with a JSON Schema in `spec/`.
- A source generator that writes the manifest at build time from `[Workload]` attributes and `AddWorkload` calls, so the CLI needs no reflection and the code stays AOT-friendly.

### Phase 4: Alert provisioning
- `RunSignal.NewRelic`: maps each expectation to a NRQL alert condition (missed, overrun, stalled, low-output, failure-rate), groups them into a policy per service, and connects Teams and Slack destinations through New Relic workflows. Severity drives routing.
- `RunSignal.Cli` (`dotnet tool`): `runsignal scan`, `runsignal diff` and `runsignal provision`. These are idempotent and only touch resources RunSignal created (tag-based ownership), so they are safe to run in CI.

### Phase 5: Other languages
Thin SDKs on each language's OpenTelemetry API, in this order:
1. **Go**: K8s CronJobs and workers; `robfig/cron` integration.
2. **Node.js**: BullMQ integration.
3. **Java**: Spring Boot starter with `@Scheduled` and Spring Batch integration.
4. **Rust**: `tokio` helpers.

A shared conformance suite runs the same scenarios against every SDK and compares the exported OTLP with golden files.

### Phase 6: More backends
- Prometheus / Grafana alert rules and Datadog monitors from the same manifest.

## Decisions

- **Notifications go through the backend.** RunSignal provisions alerts and their Teams/Slack destinations in the
  observability backend (New Relic first); it does not send notifications itself. Only the backend can notice
  that a dead process has stopped sending signals. Direct notifications for teams without a backend may come
  later as a separate service (for example `RunSignal.Server`), not inside the SDKs.

## Open decisions

- **Package names.** Do the language SDKs keep the `RunSignal.<Language>` names, or use each ecosystem's conventions (`github.com/.../runsignal-go`, `@runsignal/node`, `io.runsignal`)?
