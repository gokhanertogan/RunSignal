# RunSignal for Go

Alerts for Go background work that no HTTP status code describes: cron jobs, Kubernetes CronJobs, queue
consumers and sync loops. RunSignal records runs, items and heartbeats as OpenTelemetry metrics, with the same
names as every other RunSignal SDK, so the `runsignal` CLI provisions the same alerts for Go services as for
.NET, Java, Node.js and Rust ones. The [main README](../README.md) covers the concepts, the alerts and the
backends.

## Installation

```bash
go get github.com/gokhanertogan/RunSignal/go
```

Requires Go 1.26 or later, the minimum of OpenTelemetry Go. The only dependencies are the OpenTelemetry API,
`golang.org/x/text` and, for `rscron`, `github.com/robfig/cron/v3`.

## Quick start

```go
import "github.com/gokhanertogan/RunSignal/go"

invoices := runsignal.Default.MustWorkload("nightly-invoice",
	runsignal.Schedule("0 2 * * *"),
	runsignal.MaxDuration(30*time.Minute),
	runsignal.MinProcessed(100),
	runsignal.WithSeverity(runsignal.Critical))

err := invoices.Run(ctx, func(ctx context.Context) error {
	for _, order := range orders {
		if err := createInvoice(ctx, order); err != nil {
			return err
		}
		invoices.Processed(ctx, 1)
	}
	return nil
})
```

`runsignal.Default` records on the global OpenTelemetry providers, which forward to the SDK whenever it is
set up. For explicit providers, use `runsignal.NewRegistry(runsignal.WithMeterProvider(mp))`.

## Recording work

| Call | Records |
|---|---|
| `w.Run(ctx, fn)` | One run. It succeeds on `nil`, is cancelled when `ctx` was cancelled, and fails otherwise. A panic fails the run and is re-raised. |
| `ctx, run := w.Start(ctx)` then `run.Succeed()` / `Fail(err)` / `FailCode(code, msg)` / `Cancel()` | A run ended by hand. |
| `w.Processed(ctx, n)` / `w.ItemFailed(ctx, n)` | Items handled. |
| `w.Beat(ctx)` | A heartbeat from a long-running loop. |

`error.type` is the error's Go type, such as `*fs.PathError`, looking through `fmt.Errorf("...: %w")` wrapping.
Implement `ErrorType() string` on an error to supply a stable code instead.

## robfig/cron

```go
c := cron.New()
rscron.AddFunc(c, runsignal.Default, "nightly-invoice", "0 2 * * *",
	func(ctx context.Context) error { return generateInvoices(ctx) },
	runsignal.MaxDuration(30*time.Minute))
c.Start()
```

Every run is recorded, and the cron spec becomes the workload's schedule, so a job that never fires gets a
`missed` alert. `rscron.Wrap` returns a `cron.JobWrapper` for existing `cron.Job`s.

## Manifest

Once every workload is registered, write the manifest for `runsignal provision`. It is written only when
`RUNSIGNAL_MANIFEST_PATH` is set:

```go
if _, err := runsignal.Default.WriteManifestFromEnv("billing-service"); err != nil {
	log.Fatal(err)
}
```

## Exporting

**New Relic** needs delta temporality. With cumulative, a stopped workload never looks stopped:

```go
exporter, _ := otlpmetricgrpc.New(ctx,
	otlpmetricgrpc.WithEndpoint("otlp.eu01.nr-data.net:4317"), // US: otlp.nr-data.net:4317
	otlpmetricgrpc.WithHeaders(map[string]string{"api-key": licenseKey}),
	otlpmetricgrpc.WithTemporalitySelector(sdkmetric.DeltaTemporalitySelector))
```

**Prometheus 3** receives OTLP at `/api/v1/otlp/v1/metrics` with the default (cumulative) temporality. Generate
its rules with `runsignal export prometheus`.

A complete program is in [examples/worker](examples/worker).

## Performance

Measured on Apple Silicon with Go 1.27 and the OpenTelemetry SDK attached; the times include its aggregation.
Run `go test -bench . -run ^$`.

| Operation | Time | Allocations |
|---|---|---|
| `Run` | ~290 ns | 0 |
| `Start` + `Succeed` | ~360 ns | 0 |
| `Processed` / `Beat` | ~110 ns | 0 |
| `Start` + `Succeed` with the global tracer | ~400 ns | 3, inside OpenTelemetry |

For workloads that run thousands of times per second, create the registry with `runsignal.WithoutTracing()`.

## Links

[Specification](../spec/README.md) · [Changelog](../CHANGELOG.md) · [Other languages](../README.md#quick-start) ·
[pkg.go.dev](https://pkg.go.dev/github.com/gokhanertogan/RunSignal/go)
