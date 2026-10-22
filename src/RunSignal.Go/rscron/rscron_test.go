package rscron_test

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/robfig/cron/v3"
	"go.opentelemetry.io/otel/attribute"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"

	runsignal "github.com/gokhanertogan/RunSignal/src/RunSignal.Go"
	"github.com/gokhanertogan/RunSignal/src/RunSignal.Go/rscron"
)

func outcomes(t *testing.T, reader *sdkmetric.ManualReader) map[string]int64 {
	t.Helper()
	var rm metricdata.ResourceMetrics
	if err := reader.Collect(context.Background(), &rm); err != nil {
		t.Fatal(err)
	}
	out := map[string]int64{}
	for _, sm := range rm.ScopeMetrics {
		for _, m := range sm.Metrics {
			if m.Name != runsignal.MetricRuns {
				continue
			}
			for _, dp := range m.Data.(metricdata.Sum[int64]).DataPoints {
				name, _ := dp.Attributes.Value(attribute.Key(runsignal.AttributeWorkload))
				outcome, _ := dp.Attributes.Value(attribute.Key(runsignal.AttributeOutcome))
				out[name.AsString()+"/"+outcome.AsString()] += dp.Value
			}
		}
	}
	return out
}

func TestScheduledJobsAreRecordedAndTheirSpecBecomesTheSchedule(t *testing.T) {
	reader := sdkmetric.NewManualReader()
	registry := runsignal.NewRegistry(runsignal.WithMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))))
	c := cron.New(cron.WithSeconds())

	var calls atomic.Int32
	if _, err := rscron.AddFunc(c, registry, "price-sync", "* * * * * *", func(context.Context) error {
		if calls.Add(1) == 1 {
			return errors.New("supplier down")
		}
		return nil
	}, runsignal.WithSeverity(runsignal.Critical)); err != nil {
		t.Fatal(err)
	}
	if _, err := rscron.AddFunc(c, registry, "cleanup", "@every 1h", func(context.Context) error { return nil }); err != nil {
		t.Fatal(err)
	}

	c.Start()
	deadline := time.Now().Add(5 * time.Second)
	for calls.Load() < 2 && time.Now().Before(deadline) {
		time.Sleep(50 * time.Millisecond)
	}
	<-c.Stop().Done()

	got := outcomes(t, reader)
	if got["price-sync/failure"] != 1 || got["price-sync/success"] < 1 {
		t.Fatalf("outcomes = %v", got)
	}

	workloads := registry.Workloads()
	if len(workloads) != 2 {
		t.Fatalf("workloads = %d", len(workloads))
	}
	if e := workloads[1].Expectations(); workloads[1].Name() != "price-sync" || e.Schedule != "* * * * * *" || e.Severity != runsignal.Critical {
		t.Fatalf("price-sync expectations = %+v", e)
	}
	if e := workloads[0].Expectations(); e.Schedule != "" {
		t.Fatalf("@every descriptor became a schedule: %q", e.Schedule)
	}
}

func TestWrapRecordsPanicsAsFailures(t *testing.T) {
	reader := sdkmetric.NewManualReader()
	registry := runsignal.NewRegistry(runsignal.WithMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))))
	wrap, err := rscron.Wrap(registry, "legacy-job")
	if err != nil {
		t.Fatal(err)
	}

	wrap(cron.FuncJob(func() {})).Run()
	func() {
		defer func() { _ = recover() }()
		wrap(cron.FuncJob(func() { panic("boom") })).Run()
	}()

	if got := outcomes(t, reader); got["legacy-job/success"] != 1 || got["legacy-job/failure"] != 1 {
		t.Fatalf("outcomes = %v", got)
	}
}
