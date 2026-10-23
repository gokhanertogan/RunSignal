package runsignal_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"testing"
	"time"

	"go.opentelemetry.io/otel/attribute"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"

	runsignal "github.com/gokhanertogan/RunSignal/go"
)

type harness struct {
	reader   *sdkmetric.ManualReader
	spans    *tracetest.SpanRecorder
	registry *runsignal.Registry
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	reader := sdkmetric.NewManualReader(sdkmetric.WithTemporalitySelector(sdkmetric.DeltaTemporalitySelector))
	spans := tracetest.NewSpanRecorder()
	return &harness{
		reader: reader,
		spans:  spans,
		registry: runsignal.NewRegistry(
			runsignal.WithMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))),
			runsignal.WithTracerProvider(sdktrace.NewTracerProvider(sdktrace.WithSpanProcessor(spans)))),
	}
}

type point struct {
	value float64
	attrs attribute.Set
}

// collect returns every data point per metric name.
func (h *harness) collect(t *testing.T) map[string][]point {
	t.Helper()
	var rm metricdata.ResourceMetrics
	if err := h.reader.Collect(context.Background(), &rm); err != nil {
		t.Fatal(err)
	}
	out := map[string][]point{}
	for _, sm := range rm.ScopeMetrics {
		for _, m := range sm.Metrics {
			switch data := m.Data.(type) {
			case metricdata.Sum[int64]:
				for _, dp := range data.DataPoints {
					out[m.Name] = append(out[m.Name], point{float64(dp.Value), dp.Attributes})
				}
			case metricdata.Gauge[float64]:
				for _, dp := range data.DataPoints {
					out[m.Name] = append(out[m.Name], point{dp.Value, dp.Attributes})
				}
			case metricdata.Histogram[float64]:
				for _, dp := range data.DataPoints {
					out[m.Name] = append(out[m.Name], point{dp.Sum, dp.Attributes})
				}
			}
		}
	}
	return out
}

func attr(p point, key string) string {
	v, _ := p.attrs.Value(attribute.Key(key))
	return v.AsString()
}

func TestRunRecordsOutcomeDurationAndSpan(t *testing.T) {
	h := newHarness(t)
	w := h.registry.MustWorkload("nightly-invoice")

	if err := w.Run(context.Background(), func(ctx context.Context) error { return nil }); err != nil {
		t.Fatal(err)
	}

	m := h.collect(t)
	runs := m[runsignal.MetricRuns]
	if len(runs) != 1 || runs[0].value != 1 || attr(runs[0], runsignal.AttributeOutcome) != "success" ||
		attr(runs[0], runsignal.AttributeWorkload) != "nightly-invoice" {
		t.Fatalf("runs = %+v", runs)
	}
	if _, ok := runs[0].attrs.Value(runsignal.AttributeErrorType); ok {
		t.Fatal("successful run has error.type")
	}
	if len(m[runsignal.MetricDuration]) != 1 {
		t.Fatalf("duration = %+v", m[runsignal.MetricDuration])
	}
	ended := h.spans.Ended()
	if len(ended) != 1 || ended[0].Name() != "nightly-invoice" {
		t.Fatalf("spans = %v", ended)
	}
}

type statementMissing struct{}

func (statementMissing) Error() string     { return "no statement today" }
func (statementMissing) ErrorType() string { return "statement_missing" }

func TestFailRecordsErrorType(t *testing.T) {
	cases := []struct {
		err  error
		want string
	}{
		{&fs.PathError{Op: "open", Path: "x", Err: fs.ErrNotExist}, "*fs.PathError"},
		{fmt.Errorf("importing: %w", &fs.PathError{Op: "open", Path: "x", Err: fs.ErrNotExist}), "*fs.PathError"},
		{fmt.Errorf("both: %w, %w", &fs.PathError{}, errors.New("x")), "*fs.PathError"},
		{statementMissing{}, "statement_missing"},
		{fmt.Errorf("wrapped: %w", statementMissing{}), "statement_missing"},
		{errors.Join(errors.New("a")), "*errors.joinError"},
	}
	for _, c := range cases {
		h := newHarness(t)
		w := h.registry.MustWorkload("import")
		_ = w.Run(context.Background(), func(context.Context) error { return c.err })

		runs := h.collect(t)[runsignal.MetricRuns]
		if len(runs) != 1 || attr(runs[0], runsignal.AttributeOutcome) != "failure" || attr(runs[0], runsignal.AttributeErrorType) != c.want {
			t.Errorf("%T: runs = %+v, want error.type %s", c.err, runs, c.want)
		}
	}
}

func TestRunIsCancelledOnlyWhenItsContextIsCancelled(t *testing.T) {
	h := newHarness(t)
	w := h.registry.MustWorkload("sync")

	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_ = w.Run(ctx, func(ctx context.Context) error { return ctx.Err() })
	_ = w.Run(context.Background(), func(context.Context) error { return context.Canceled }) // someone else's cancellation

	outcomes := map[string]float64{}
	for _, p := range h.collect(t)[runsignal.MetricRuns] {
		outcomes[attr(p, runsignal.AttributeOutcome)] += p.value
	}
	if outcomes["cancelled"] != 1 || outcomes["failure"] != 1 {
		t.Fatalf("outcomes = %v", outcomes)
	}
}

func TestRunFailsAndRepanicsOnPanic(t *testing.T) {
	h := newHarness(t)
	w := h.registry.MustWorkload("panicky")
	func() {
		defer func() {
			if recover() == nil {
				t.Fatal("panic was swallowed")
			}
		}()
		_ = w.Run(context.Background(), func(context.Context) error { panic("boom") })
	}()

	if runs := h.collect(t)[runsignal.MetricRuns]; len(runs) != 1 || attr(runs[0], runsignal.AttributeOutcome) != "failure" {
		t.Fatalf("runs = %+v", runs)
	}
}

func TestZeroRunIsANoOp(t *testing.T) {
	var run runsignal.Run
	run.Succeed()
	run.Fail(errors.New("x"))
	run.Cancel()
	if run.Elapsed() != 0 {
		t.Fatal("zero run has elapsed time")
	}
}

func TestWithoutTracingRecordsMetricsButNoSpans(t *testing.T) {
	reader := sdkmetric.NewManualReader()
	spans := tracetest.NewSpanRecorder()
	r := runsignal.NewRegistry(
		runsignal.WithMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))),
		runsignal.WithTracerProvider(sdktrace.NewTracerProvider(sdktrace.WithSpanProcessor(spans))),
		runsignal.WithoutTracing())

	_ = r.MustWorkload("consumer").Run(context.Background(), func(context.Context) error { return nil })

	h := &harness{reader: reader}
	if runs := h.collect(t)[runsignal.MetricRuns]; len(runs) != 1 {
		t.Fatalf("runs = %+v", runs)
	}
	if len(spans.Ended()) != 0 {
		t.Fatalf("spans = %d", len(spans.Ended()))
	}
}

func TestItemsHeartbeatsAndSinceGauges(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	scheduled := h.registry.MustWorkload("scheduled", runsignal.Schedule("0 2 * * *"))
	loop := h.registry.MustWorkload("loop", runsignal.ExpectEvery(5*time.Minute))
	h.registry.MustWorkload("bare")

	scheduled.Processed(ctx, 10)
	scheduled.ItemFailed(ctx, 2)
	time.Sleep(50 * time.Millisecond)
	loop.Beat(ctx)

	m := h.collect(t)
	items := map[string]float64{}
	for _, p := range m[runsignal.MetricItems] {
		items[attr(p, runsignal.AttributeItemResult)] += p.value
	}
	if items["processed"] != 10 || items["failed"] != 2 {
		t.Fatalf("items = %v", items)
	}
	if len(m[runsignal.MetricHeartbeats]) != 1 {
		t.Fatalf("heartbeats = %+v", m[runsignal.MetricHeartbeats])
	}

	// A scheduled workload reports its age before its first run; an unscheduled one does not.
	since := m[runsignal.MetricSinceSuccess]
	if len(since) != 1 || attr(since[0], runsignal.AttributeWorkload) != "scheduled" || since[0].value < 0.04 {
		t.Fatalf("since_success = %+v", since)
	}
	beat := m[runsignal.MetricSinceHeartbeat]
	if len(beat) != 1 || attr(beat[0], runsignal.AttributeWorkload) != "loop" || beat[0].value > 0.04 {
		t.Fatalf("since_heartbeat = %+v", beat)
	}
}

func TestRegistryValidatesNamesAndOptions(t *testing.T) {
	r := runsignal.NewRegistry()
	for _, bad := range []string{"", "Nightly", "has space", "-dash", "order/{id}"} {
		if _, err := r.Workload(bad); !errors.Is(err, runsignal.ErrInvalidName) {
			t.Errorf("%q: err = %v", bad, err)
		}
	}
	if _, err := r.Workload("rate", runsignal.FailureRateThreshold(1.5)); err == nil {
		t.Error("failure rate 1.5 accepted")
	}

	first := r.MustWorkload("sync", runsignal.WithSeverity(runsignal.Critical))
	if again, _ := r.Workload("sync"); again != first {
		t.Error("same name returned a different workload")
	}
	if _, err := r.Workload("sync", runsignal.WithSeverity(runsignal.Info)); err == nil {
		t.Error("reconfiguring an existing workload was accepted")
	}
	if kept, _ := r.GetOrAdd("sync", runsignal.WithSeverity(runsignal.Info)); kept.Expectations().Severity != runsignal.Critical {
		t.Error("GetOrAdd overrode options declared first")
	}
}

// The vectors every SDK must agree on.
func TestToWorkloadNameConformance(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "spec", "conformance", "workload-names.json"))
	if err != nil {
		t.Fatal(err)
	}
	var vectors struct {
		Names []struct {
			Input    string `json:"input"`
			Expected string `json:"expected"`
		} `json:"names"`
		Invalid []string `json:"invalid"`
	}
	if err := json.Unmarshal(data, &vectors); err != nil {
		t.Fatal(err)
	}

	for _, v := range vectors.Names {
		if got, err := runsignal.ToWorkloadName(v.Input); err != nil || got != v.Expected {
			t.Errorf("ToWorkloadName(%q) = %q, %v; want %q", v.Input, got, err, v.Expected)
		}
	}
	for _, input := range vectors.Invalid {
		if _, err := runsignal.ToWorkloadName(input); err == nil {
			t.Errorf("ToWorkloadName(%q) accepted", input)
		}
	}
}

func TestManifestMatchesTheSchema(t *testing.T) {
	r := runsignal.NewRegistry()
	r.MustWorkload("stock-sync", runsignal.ExpectEvery(5*time.Minute), runsignal.FailureRateThreshold(0.2))
	r.MustWorkload("nightly-invoice", runsignal.Schedule("0 2 * * *"), runsignal.MaxDuration(30*time.Minute),
		runsignal.MinProcessed(100), runsignal.WithSeverity(runsignal.Critical))

	path := filepath.Join(t.TempDir(), "out", "manifest.json")
	t.Setenv("RUNSIGNAL_MANIFEST_PATH", path)
	if written, err := r.WriteManifestFromEnv("billing"); err != nil || written != path {
		t.Fatalf("written = %q, err = %v", written, err)
	}
	data, _ := os.ReadFile(path)

	want := `{
  "version": 1,
  "service": "billing",
  "workloads": [
    {
      "name": "nightly-invoice",
      "schedule": "0 2 * * *",
      "maxDurationSeconds": 1800,
      "minProcessed": 100,
      "severity": "critical"
    },
    {
      "name": "stock-sync",
      "expectEverySeconds": 300,
      "failureRateThreshold": 0.2,
      "severity": "warning"
    }
  ]
}`
	if string(data) != want {
		t.Fatalf("manifest:\n%s\nwant:\n%s", data, want)
	}
}
