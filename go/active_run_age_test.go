package runsignal_test

import (
	"context"
	"errors"
	"runtime"
	"testing"
	"time"

	sdkmetric "go.opentelemetry.io/otel/sdk/metric"

	runsignal "github.com/gokhanertogan/RunSignal/go"
)

// activeRunAge returns the active_run_age per workload.
func activeRunAge(t *testing.T, h *harness) map[string]float64 {
	t.Helper()
	out := map[string]float64{}
	for _, p := range h.collect(t)[runsignal.MetricActiveRunAge] {
		out[attr(p, runsignal.AttributeWorkload)] = p.value
	}
	return out
}

func TestActiveRunAgeGrowsWhileARunIsInProgress(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	w := h.registry.MustWorkload("job")

	_, run := w.Start(ctx)
	time.Sleep(50 * time.Millisecond)
	if age := activeRunAge(t, h); age["job"] < 0.04 {
		t.Fatalf("active_run_age while running = %v", age)
	}

	run.Succeed()
	if age, ok := activeRunAge(t, h)["job"]; !ok || age != 0 {
		t.Fatalf("active_run_age after the run = %v, reported %v", age, ok)
	}
}

func TestActiveRunAgeEndsOnEveryOutcome(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	w := h.registry.MustWorkload("job")

	for _, end := range []func(runsignal.Run){
		runsignal.Run.Succeed,
		runsignal.Run.Cancel,
		func(r runsignal.Run) { r.Fail(errors.New("boom")) },
		func(r runsignal.Run) { r.FailCode("missing", "") },
	} {
		_, run := w.Start(ctx)
		end(run)
	}
	func() {
		defer func() { _ = recover() }()
		_ = w.Run(ctx, func(context.Context) error { panic("boom") })
	}()

	if age := activeRunAge(t, h)["job"]; age != 0 {
		t.Fatalf("active_run_age = %v", age)
	}
}

func TestActiveRunAgeIsReportedForMaxDurationOrStartedRuns(t *testing.T) {
	h := newHarness(t)
	h.registry.MustWorkload("bounded", runsignal.MaxDuration(time.Minute))
	h.registry.MustWorkload("idle", runsignal.Schedule("0 2 * * *"))

	age := activeRunAge(t, h)
	if len(age) != 1 || age["bounded"] != 0 {
		t.Fatalf("active_run_age = %v", age)
	}
}

func TestEndingARunTwiceDoesNotEndAnotherRun(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	w := h.registry.MustWorkload("job")

	_, first := w.Start(ctx)
	first.Succeed()
	_, second := w.Start(ctx) // reuses the slot the first run freed
	first.Succeed()
	first.Cancel()
	time.Sleep(20 * time.Millisecond)

	if age := activeRunAge(t, h)["job"]; age < 0.015 {
		t.Fatalf("active_run_age = %v; the second run was ended by the first", age)
	}
	second.Succeed()
	if age := activeRunAge(t, h)["job"]; age != 0 {
		t.Fatalf("active_run_age = %v", age)
	}
}

func TestActiveRunAgeWithMoreRunsThanSlots(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	w := h.registry.MustWorkload("job")

	_, oldest := w.Start(ctx)
	time.Sleep(30 * time.Millisecond)
	runs := make([]runsignal.Run, 100)
	for i := range runs {
		_, runs[i] = w.Start(ctx)
	}
	if age := activeRunAge(t, h)["job"]; age < 0.025 {
		t.Fatalf("active_run_age = %v", age)
	}

	oldest.Succeed()
	for _, run := range runs {
		run.Succeed()
	}
	if age := activeRunAge(t, h)["job"]; age != 0 {
		t.Fatalf("active_run_age = %v", age)
	}
	// The slots are free again.
	_, run := w.Start(ctx)
	time.Sleep(20 * time.Millisecond)
	if age := activeRunAge(t, h)["job"]; age < 0.015 {
		t.Fatalf("active_run_age = %v", age)
	}
	run.Succeed()
}

func TestStartAndEndDoNotAllocate(t *testing.T) {
	mp := sdkmetric.NewMeterProvider(sdkmetric.WithReader(sdkmetric.NewManualReader()))
	w := runsignal.NewRegistry(runsignal.WithMeterProvider(mp), runsignal.WithoutTracing()).MustWorkload("job")
	ctx := context.Background()
	_, warm := w.Start(ctx) // the tracking slots are allocated once, on the first run
	warm.Succeed()

	if allocs := testing.AllocsPerRun(100, func() {
		_, run := w.Start(ctx)
		run.Succeed()
	}); allocs != 0 {
		t.Fatalf("allocs per run = %v", allocs)
	}
}

// runtime.Goexit (t.FailNow, for example) neither returns nor panics; the run must still end.
func TestRunThatCallsGoexitFailsInsteadOfStayingInProgress(t *testing.T) {
	h := newHarness(t)
	w := h.registry.MustWorkload("job")

	done := make(chan struct{})
	go func() {
		defer close(done)
		_ = w.Run(context.Background(), func(context.Context) error {
			runtime.Goexit()
			return nil
		})
	}()
	<-done

	metrics := h.collect(t)
	for _, p := range metrics[runsignal.MetricActiveRunAge] {
		if p.value != 0 {
			t.Fatalf("active_run_age after Goexit = %v", p.value)
		}
	}
	runs := metrics[runsignal.MetricRuns]
	if len(runs) != 1 || attr(runs[0], runsignal.AttributeOutcome) != "failure" || attr(runs[0], runsignal.AttributeErrorType) != "goexit" {
		t.Fatalf("runs after Goexit = %+v", runs)
	}
}
