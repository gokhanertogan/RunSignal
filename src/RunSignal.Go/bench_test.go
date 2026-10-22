package runsignal_test

import (
	"context"
	"testing"

	sdkmetric "go.opentelemetry.io/otel/sdk/metric"

	runsignal "github.com/gokhanertogan/RunSignal/src/RunSignal.Go"
)

// Hot-path cost with an OpenTelemetry SDK meter provider attached. Tracing is off unless the benchmark says
// otherwise, because the global tracer allocates a span and a context per run even when nothing records them.
func benchmarkWorkload(b *testing.B, opts ...runsignal.RegistryOption) *runsignal.Workload {
	b.Helper()
	mp := sdkmetric.NewMeterProvider(sdkmetric.WithReader(sdkmetric.NewManualReader()))
	opts = append([]runsignal.RegistryOption{runsignal.WithMeterProvider(mp)}, opts...)
	return runsignal.NewRegistry(opts...).MustWorkload("benchmark")
}

func BenchmarkStartSucceedWithGlobalTracer(b *testing.B) {
	w := benchmarkWorkload(b)
	ctx := context.Background()
	b.ReportAllocs()
	for b.Loop() {
		_, run := w.Start(ctx)
		run.Succeed()
	}
}

func BenchmarkStartSucceed(b *testing.B) {
	w := benchmarkWorkload(b, runsignal.WithoutTracing())
	ctx := context.Background()
	b.ReportAllocs()
	for b.Loop() {
		_, run := w.Start(ctx)
		run.Succeed()
	}
}

func BenchmarkRun(b *testing.B) {
	w := benchmarkWorkload(b, runsignal.WithoutTracing())
	ctx := context.Background()
	fn := func(context.Context) error { return nil }
	b.ReportAllocs()
	for b.Loop() {
		_ = w.Run(ctx, fn)
	}
}

func BenchmarkProcessed(b *testing.B) {
	w := benchmarkWorkload(b)
	ctx := context.Background()
	b.ReportAllocs()
	for b.Loop() {
		w.Processed(ctx, 1)
	}
}

func BenchmarkBeat(b *testing.B) {
	w := benchmarkWorkload(b)
	ctx := context.Background()
	b.ReportAllocs()
	for b.Loop() {
		w.Beat(ctx)
	}
}
