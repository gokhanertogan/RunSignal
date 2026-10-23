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
- Quartz.NET 4 ✅: `RunSignal.Quartz4` is the same sources built against Quartz 4.0 (`#if QUARTZ4`), for .NET 10.
  - Quartz 4 removed per-execution context storage, so runs are tracked by scheduler name and fire instance ID. Quartz 3 now works the same way.
  - Quartz 4 refers to job classes by name (`JobType`) and registers a factory per named scheduler, so discovery reads every scheduler from `ISchedulerRepository`.
  - The package is tested against Quartz 4.6 in its own test project, including two schedulers whose fire IDs could collide.

### Phase 3: Workload manifest ✅
- A JSON manifest of every workload and its expectations ([spec/manifest.schema.json](../spec/manifest.schema.json)), serialized with System.Text.Json source generation (AOT-friendly, no new dependency in Core).
- `IWorkloadDiscovery`: at startup, `AddHangfire()` registers every recurring job and `AddQuartz()` registers every scheduled job, so jobs that have never run get `missed` alerts. Discovery failures are logged and never stop the application.
- The manifest is written after startup when `RUNSIGNAL_MANIFEST_PATH` (or `RunSignalOptions.ManifestPath`) is set. It is written atomically, sorted, and without timestamps, so it diffs cleanly.
- Changed from the original plan: the manifest comes from the running application instead of a build-time source generator. A generator cannot see Hangfire recurring jobs registered at runtime, cron triggers from configuration, or `AddWorkload` lambdas, so it would produce an incomplete manifest.

### Phase 4: Alert provisioning ✅
- `RunSignal.NewRelic`:
  - `AlertPlanner` turns a manifest into NRQL conditions (missed, failed, stalled, overrun, low output, failure rate, item failure rate). It runs offline and needs no API.
  - `NewRelicProvisioner` applies the plan through NerdGraph: one policy per service and severity, conditions marked as managed, and workflows routed to the channel IDs you give. It is idempotent and never touches conditions it did not create.
- `RunSignal.Cli` (`dotnet tool`, command `runsignal`): `plan`, `diff` and `provision`, which read one or more manifests. Expected errors print one line, not a stack trace.
- Spec change: `last_success` was replaced by the `since_success` and `since_heartbeat` gauges. New Relic's loss of signal only fires for signals that reported at least once, so a job that never runs could not otherwise be alerted on. The gauges count from registration until the first success or heartbeat. Delta temporality is now required, because with cumulative temporality a backend never sees a signal stop.
- Verification: the API shapes and limits were checked against New Relic's Terraform provider, Go client and docs. Provisioning is tested end to end against a stateful in-memory NerdGraph, including idempotency.
- Live test on a real New Relic account (EU), using the sample worker:
  - All five metric types arrived over OTLP with delta temporality.
  - `provision` created the policies and conditions, and a second run reported no changes.
  - A failed run opened the `failed` incident.
  - Stopping the process opened `missed` and `stalled` through loss of signal after 15 minutes.
  - The live API also showed that EVENT_FLOW conditions require `aggregationDelay`, and that NerdGraph puts error details in `extensions`, which the CLI now prints.
- Follow-ups:
  - Not yet seen firing live: `overrun`, `low output`, the failure rates, and `missed` from its threshold alone (as opposed to from loss of signal).
  - Low output is approximate for runs longer than its one-hour window.
  - Overrun only sees runs that finish (a hung run shows up as missed or stalled).
  - Workflows match one policy each.

### Phase 5: Other languages
Thin SDKs on each language's OpenTelemetry API. Shared conformance vectors in `spec/conformance/` keep them in
agreement; the first ones cover workload naming.

1. **Go** ✅ ([src/RunSignal.Go](../src/RunSignal.Go)):
   - The same instruments, attributes and manifest as .NET.
   - `Workload.Run` with cancellation and panic handling, an `ErrorTyper` interface for stable error codes, and `rscron` for robfig/cron.
   - No allocations on the hot path. `WithoutTracing()` is for workloads that run thousands of times per second, because the global tracer allocates per run.
   - Verified live: a Go service exported the same metrics to New Relic, and the .NET `runsignal` CLI provisioned its alerts unchanged.
   - Spec fix found on the way: 6-field cron now means seconds first with Unix day numbers (0 = Sunday), as robfig/cron, Spring and Cronos use. The Quartz integration converts Quartz's 1 = Sunday format before writing the manifest.
2. **Node.js** ✅ ([src/RunSignal.Node](../src/RunSignal.Node), `@runsignal/node`):
   - TypeScript, ESM.
   - `workload.run` with AbortSignal cancellation; error types from `errorType`, then `code`, then the class name.
   - BullMQ: an `instrument` processor wrapper and `discoverSchedulers`, which registers job schedulers with their cron patterns before they fire.
   - Tests use a real Redis.
   - Verified live: the .NET CLI provisioned its alerts unchanged.
   - Found live: OpenTelemetry JS's global meter provider is not a proxy, so a registry created by an import before the SDK started recorded nothing. The registry now follows the global provider.
   - Found on the way: BullMQ 6 needs a constructed ioredis client in ESM.
3. **Java** ✅ ([src/RunSignal.Java](../src/RunSignal.Java)):
   - `runsignal` core, which depends only on `opentelemetry-api`, and `runsignal-spring-boot-starter`.
   - The starter records every `@Scheduled` run through Spring's own scheduling observations, with no proxies. It registers cron jobs at startup, resolving placeholders and macros, and writes the manifest from `runsignal.manifest-path`.
   - Tested against Spring Boot 3.2, 3.5 and 4.1.
   - Verified live: the .NET CLI provisioned its alerts unchanged.
   - Lazy `GlobalOpenTelemetry` resolution avoids fixing the global instance to a no-op before the agent starts.
   - Follow-up: Spring Batch.
4. **Rust** ✅ ([src/RunSignal.Rust](../src/RunSignal.Rust), `runsignal` crate):
   - Depends only on the `opentelemetry` API and `unicode-normalization`.
   - `run` and `run_async`; a dropped future records a cancelled run.
   - Runs are ended by value, so a run cannot be recorded twice.
   - Verified live: the .NET CLI provisioned its alerts unchanged.
   - Found on the way: `std::any::type_name` changed between Rust versions (`std::io::Error` is now `core::io::error::Error`), so `error.type` from type names is documented as unstable, and `fail_code` is the stable alternative.
   - Follow-up: a `tokio-cron-scheduler` integration.

All five SDKs are now live-verified against New Relic, each provisioned by the same CLI from its own manifest.

Still to do: extend the conformance suite beyond names, by running the same scenarios against every SDK and comparing the exported OTLP with golden files.

### Phase 6: More backends
- **Prometheus / Grafana** ✅:
  - `runsignal export prometheus` writes alerting rules for Prometheus, Grafana Mimir or any Prometheus-compatible ruler.
  - Thresholds come from the shared `RunSignal.Alerting` package, so they match New Relic.
  - Verified against a local Prometheus 3.15 with its OTLP receiver: the metric names were taken from real OTLP ingestion, all rules evaluated healthy, `RunSignalFailed` fired on live failures, and `RunSignalNotReporting` fired when the process stopped.
  - `promtool test rules` covers every rule, including the first failure of a job, which `increase()` alone misses because the counter series is created by that failure.
- **Datadog monitors**: next. It needs a Datadog account to verify against.

### Phase 7: Notifications end to end (last)
- Create notification channels from an existing Teams/Slack destination ID. The New Relic UI only creates channels inside workflows and does not show their IDs, so `--channel <id>` is awkward.
- Verify a real Teams and Slack notification arriving from a RunSignal workflow.

### Also done
- `runsignal destroy` removes what RunSignal created for a service, keeps conditions added by hand and never deletes notification channels. Verified live by cleaning up the five sample services.

