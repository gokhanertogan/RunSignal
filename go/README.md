# RunSignal for Go

Monitor Go background work that no HTTP status code describes, such as cron jobs, Kubernetes CronJobs, queue
consumers and sync loops, with OpenTelemetry. It emits the same signals and manifest as every other RunSignal
SDK, so the `runsignal` CLI provisions the same New Relic alerts for Go services as for .NET ones. See the
[main README](../../README.md) for the concepts and the alerts.

```bash
go get github.com/gokhanertogan/RunSignal/go
```

Requires Go 1.26 or later, because OpenTelemetry Go requires it.

## Usage

```go
reg := runsignal.Default // records on the global OpenTelemetry providers

invoices := reg.MustWorkload("nightly-invoice",
	runsignal.Schedule("0 2 * * *"),
	runsignal.MaxDuration(30*time.Minute),
	runsignal.MinProcessed(100),
	runsignal.WithSeverity(runsignal.Critical))

// One run: succeeds on nil, is cancelled if ctx was cancelled, fails otherwise (panics fail and re-panic).
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

A long-running loop sends heartbeats, so a loop that is alive but stuck is noticed:

```go
sync := reg.MustWorkload("stock-sync", runsignal.ExpectEvery(5*time.Minute))
for ctx.Err() == nil {
	syncStock(ctx)
	sync.Beat(ctx)
	time.Sleep(time.Minute)
}
```

Manual runs, and failures without an error value:

```go
ctx, run := imports.Start(ctx)
file, ok := fetchStatement(ctx)
if !ok {
	run.FailCode("statement_missing", "no bank statement today")
	return
}
run.Succeed()
```

`error.type` is the error's Go type, such as `*fs.PathError`, with `fmt.Errorf("...: %w")` wrapping looked
through. Implement `ErrorType() string` on an error to supply a stable code instead.

### robfig/cron

```go
c := cron.New()
rscron.AddFunc(c, runsignal.Default, "nightly-invoice", "0 2 * * *",
	func(ctx context.Context) error { return generateInvoices(ctx) },
	runsignal.MaxDuration(30*time.Minute))
c.Start()
```

Every run is recorded, and the cron spec becomes the workload's schedule, so a job that never fires gets a
`missed` alert. `rscron.Wrap` returns a `cron.JobWrapper` for existing `cron.Job`s.

### Manifest

Write the manifest once every workload is registered. With `RUNSIGNAL_MANIFEST_PATH` set it is written there;
otherwise nothing happens:

```go
if _, err := runsignal.Default.WriteManifestFromEnv("billing-service"); err != nil {
	log.Fatal(err)
}
```

### Exporting to New Relic

Use delta temporality. With cumulative, a stopped workload never looks stopped:

```go
exporter, _ := otlpmetricgrpc.New(ctx,
	otlpmetricgrpc.WithEndpoint("otlp.eu01.nr-data.net:4317"), // US: otlp.nr-data.net:4317
	otlpmetricgrpc.WithHeaders(map[string]string{"api-key": licenseKey}),
	otlpmetricgrpc.WithTemporalitySelector(sdkmetric.DeltaTemporalitySelector))
```

A complete program is in [examples/worker](examples/worker).

## Performance

Measured on Apple Silicon with Go 1.27 and the OpenTelemetry SDK attached. The times include the SDK's own
aggregation. Run the benchmarks with `go test -bench . -run ^$`.

| Operation | Time | Allocations |
|---|---|---|
| `Start` + `Succeed` | ~360 ns | 0 |
| `Run` | ~290 ns | 0 |
| `Processed` / `Beat` | ~110 ns | 0 |
| `Start` + `Succeed` with the global tracer | ~400 ns | 3 (inside OpenTelemetry) |

The global tracer allocates a span and a context per run even when nothing records them. For workloads that
run thousands of times per second, such as per-message consumers, create the registry with
`runsignal.NewRegistry(runsignal.WithoutTracing())`.
