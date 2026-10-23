package runsignal_test

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"testing"
	"time"

	"go.opentelemetry.io/otel/attribute"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"

	runsignal "github.com/gokhanertogan/RunSignal/go"
)

type conformancePoint struct {
	Attributes map[string]string `json:"attributes"`
	Value      *float64          `json:"value,omitempty"`
	Count      *float64          `json:"count,omitempty"`
}

type conformanceMetric struct {
	Name   string             `json:"name"`
	Type   string             `json:"type"`
	Unit   string             `json:"unit"`
	Bounds []float64          `json:"bounds,omitempty"`
	Points []conformancePoint `json:"points"`
}

type conformanceSpec struct {
	Workloads []struct {
		Name                 string              `json:"name"`
		Schedule             string              `json:"schedule"`
		MaxDurationSeconds   float64             `json:"maxDurationSeconds"`
		ExpectEverySeconds   float64             `json:"expectEverySeconds"`
		MinProcessed         int64               `json:"minProcessed"`
		FailureRateThreshold float64             `json:"failureRateThreshold"`
		Severity             *runsignal.Severity `json:"severity"`
	} `json:"workloads"`
	Actions []struct {
		Workload string `json:"workload"`
		Action   string `json:"action"`
		Code     string `json:"code"`
		Count    int64  `json:"count"`
	} `json:"actions"`
	Expected []conformanceMetric `json:"expected"`
}

// spec/conformance/signals.json: every SDK runs the same scenario and must emit exactly the same metrics.
func TestConformanceSignals(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "spec", "conformance", "signals.json"))
	if err != nil {
		t.Fatal(err)
	}
	var spec conformanceSpec
	if err := json.Unmarshal(data, &spec); err != nil {
		t.Fatal(err)
	}

	reader := sdkmetric.NewManualReader()
	registry := runsignal.NewRegistry(runsignal.WithMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))))
	for _, w := range spec.Workloads {
		var opts []runsignal.Option
		if w.Schedule != "" {
			opts = append(opts, runsignal.Schedule(w.Schedule))
		}
		if w.MaxDurationSeconds > 0 {
			opts = append(opts, runsignal.MaxDuration(time.Duration(w.MaxDurationSeconds*float64(time.Second))))
		}
		if w.ExpectEverySeconds > 0 {
			opts = append(opts, runsignal.ExpectEvery(time.Duration(w.ExpectEverySeconds*float64(time.Second))))
		}
		if w.MinProcessed > 0 {
			opts = append(opts, runsignal.MinProcessed(w.MinProcessed))
		}
		if w.FailureRateThreshold > 0 {
			opts = append(opts, runsignal.FailureRateThreshold(w.FailureRateThreshold))
		}
		if w.Severity != nil {
			opts = append(opts, runsignal.WithSeverity(*w.Severity))
		}
		registry.MustWorkload(w.Name, opts...)
	}

	ctx := context.Background()
	for _, a := range spec.Actions {
		w := registry.MustWorkload(a.Workload)
		switch a.Action {
		case "succeed":
			_, run := w.Start(ctx)
			run.Succeed()
		case "failCode":
			_, run := w.Start(ctx)
			run.FailCode(a.Code, "")
		case "cancel":
			_, run := w.Start(ctx)
			run.Cancel()
		case "processed":
			w.Processed(ctx, a.Count)
		case "itemFailed":
			w.ItemFailed(ctx, a.Count)
		case "beat":
			w.Beat(ctx)
		default:
			t.Fatalf("unknown action %q", a.Action)
		}
	}

	var rm metricdata.ResourceMetrics
	if err := reader.Collect(ctx, &rm); err != nil {
		t.Fatal(err)
	}
	var got []conformanceMetric
	for _, sm := range rm.ScopeMetrics {
		for _, m := range sm.Metrics {
			cm := conformanceMetric{Name: m.Name, Unit: m.Unit}
			switch d := m.Data.(type) {
			case metricdata.Sum[int64]:
				cm.Type = "counter"
				for _, dp := range d.DataPoints {
					v := float64(dp.Value)
					cm.Points = append(cm.Points, conformancePoint{Attributes: attrMap(dp.Attributes), Value: &v})
				}
			case metricdata.Histogram[float64]:
				cm.Type = "histogram"
				for _, dp := range d.DataPoints {
					c := float64(dp.Count)
					cm.Bounds = dp.Bounds
					cm.Points = append(cm.Points, conformancePoint{Attributes: attrMap(dp.Attributes), Count: &c})
				}
			case metricdata.Gauge[float64]:
				cm.Type = "gauge"
				for _, dp := range d.DataPoints {
					cm.Points = append(cm.Points, conformancePoint{Attributes: attrMap(dp.Attributes)})
				}
			default:
				cm.Type = reflect.TypeOf(m.Data).String()
			}
			if len(cm.Points) > 0 {
				got = append(got, cm)
			}
		}
	}

	if want, have := canonical(t, spec.Expected), canonical(t, got); want != have {
		t.Fatalf("signals differ from spec/conformance/signals.json\nwant:\n%s\ngot:\n%s", want, have)
	}
}

func attrMap(set attribute.Set) map[string]string {
	m := map[string]string{}
	for _, kv := range set.ToSlice() {
		m[string(kv.Key)] = kv.Value.Emit()
	}
	return m
}

// Order-insensitive form: metrics sorted by name, points by their attributes.
func canonical(t *testing.T, metrics []conformanceMetric) string {
	t.Helper()
	sorted := append([]conformanceMetric(nil), metrics...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Name < sorted[j].Name })
	for i := range sorted {
		points := append([]conformancePoint(nil), sorted[i].Points...)
		key := func(p conformancePoint) string { b, _ := json.Marshal(p.Attributes); return string(b) }
		sort.Slice(points, func(a, b int) bool { return key(points[a]) < key(points[b]) })
		sorted[i].Points = points
	}
	out, err := json.MarshalIndent(sorted, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	return string(out)
}
