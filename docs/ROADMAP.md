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

### Phase 2: Scheduler integrations (.NET) ✅
- `[Workload]` attribute in Core (name, schedule, durations such as `"30m"`, minimum output, failure rate, severity) and readable name derivation from any job identifier.
- `RunSignal.Hangfire` (Hangfire 1.8+): a server filter that records every job run, unwraps Hangfire's exception wrapper, treats shutdown and aborted jobs as cancelled, and names recurring jobs after their ID with their cron as the schedule. Resolution is cached per job, so a run costs no reflection or extra storage access.
- `RunSignal.Quartz` (Quartz.NET 3.x): a job listener that does the same, with schedules read from cron triggers.
- Item counting from job code through `context.GetWorkload()`.
- Integration tests against a real Hangfire server (in-memory storage) and a real Quartz scheduler.
- Follow-up: Quartz.NET 4 support. Version 4 changed `IJob` and the listener interfaces to `ValueTask`, so it needs its own build (multi-targeted package or `RunSignal.Quartz4`).

### Phase 3: Workload manifest ✅
- A JSON manifest of every workload and its expectations ([spec/manifest.schema.json](../spec/manifest.schema.json)), serialized with System.Text.Json source generation (AOT-friendly, no new dependency in Core).
- `IWorkloadDiscovery`: at startup, `AddHangfire()` registers every recurring job and `AddQuartz()` registers every scheduled job, so jobs that have never run get `missed` alerts. Discovery failures are logged and never stop the application.
- The manifest is written after startup when `RUNSIGNAL_MANIFEST_PATH` (or `RunSignalOptions.ManifestPath`) is set. It is written atomically, sorted, and without timestamps, so it diffs cleanly.
- Changed from the original plan: the manifest comes from the running application instead of a build-time source generator. A generator cannot see Hangfire recurring jobs registered at runtime, cron triggers from configuration, or `AddWorkload` lambdas, so it would produce an incomplete manifest.

### Phase 4: Alert provisioning
- `RunSignal.NewRelic`: maps each expectation to a NRQL alert condition (missed, overrun, stalled, low-output, failure-rate), groups them into a policy per service, and connects Teams and Slack destinations through New Relic workflows. Severity drives routing.
- `RunSignal.Cli` (`dotnet tool`): `runsignal diff` and `runsignal provision`, which read one or more manifests. These are idempotent and only touch resources RunSignal created (tag-based ownership), so they are safe to run in CI.

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
