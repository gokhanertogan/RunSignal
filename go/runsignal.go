// Package runsignal records runs, items and heartbeats of background work (scheduled jobs, queue consumers,
// sync loops) as OpenTelemetry metrics and spans, so you can alert on work that no HTTP status code describes:
// missed, overrunning, stalled or low-output jobs.
//
// The metric names, attributes and manifest format are the same in every RunSignal SDK (see spec/README.md in
// the repository), so one set of alert rules covers .NET, Go, Java, Node.js and Rust services.
//
//	reg := runsignal.NewRegistry()
//	invoices := reg.MustWorkload("nightly-invoice",
//		runsignal.Schedule("0 2 * * *"),
//		runsignal.MaxDuration(30*time.Minute),
//		runsignal.WithSeverity(runsignal.Critical))
//
//	err := invoices.Run(ctx, func(ctx context.Context) error {
//		for _, order := range orders {
//			if err := createInvoice(ctx, order); err != nil {
//				return err
//			}
//			invoices.Processed(ctx, 1)
//		}
//		return nil
//	})
package runsignal

// Names of the meter, tracer, instruments and attributes RunSignal emits. They are identical in every SDK.
const (
	// ScopeName is the name of the OpenTelemetry meter and tracer.
	ScopeName = "RunSignal"

	MetricRuns           = "runsignal.workload.runs"
	MetricDuration       = "runsignal.workload.duration"
	MetricItems          = "runsignal.workload.items"
	MetricHeartbeats     = "runsignal.workload.heartbeats"
	MetricSinceSuccess   = "runsignal.workload.since_success"
	MetricSinceHeartbeat = "runsignal.workload.since_heartbeat"

	AttributeWorkload   = "runsignal.workload"
	AttributeOutcome    = "runsignal.outcome"
	AttributeItemResult = "runsignal.item.result"
	AttributeErrorType  = "error.type"
)

// Version is the SDK version reported as the instrumentation scope version.
const Version = "0.2.0"

// DurationBuckets are the histogram bucket boundaries, in seconds, for MetricDuration: background work runs from
// milliseconds to hours, while the OpenTelemetry defaults stop at 10 seconds.
var DurationBuckets = []float64{0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800}
