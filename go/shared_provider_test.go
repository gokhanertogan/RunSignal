package runsignal_test

import (
	"context"
	"testing"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"

	runsignal "github.com/gokhanertogan/RunSignal/go"
)

// gaugeStreams returns, per gauge name, how many metric streams were exported under that name and the workloads
// of their data points.
func gaugeStreams(t *testing.T, reader sdkmetric.Reader) (map[string]int, map[string][]string) {
	t.Helper()
	var rm metricdata.ResourceMetrics
	if err := reader.Collect(context.Background(), &rm); err != nil {
		t.Fatal(err)
	}
	streams := map[string]int{}
	workloads := map[string][]string{}
	for _, sm := range rm.ScopeMetrics {
		for _, m := range sm.Metrics {
			streams[m.Name]++
			if g, ok := m.Data.(metricdata.Gauge[float64]); ok {
				for _, dp := range g.DataPoints {
					v, _ := dp.Attributes.Value(attribute.Key(runsignal.AttributeWorkload))
					workloads[m.Name] = append(workloads[m.Name], v.AsString())
				}
			}
		}
	}
	return streams, workloads
}

func assertBothWorkloads(t *testing.T, reader sdkmetric.Reader, first, second string) {
	t.Helper()
	streams, workloads := gaugeStreams(t, reader)
	for _, name := range []string{runsignal.MetricSinceSuccess, runsignal.MetricActiveRunAge} {
		if streams[name] != 1 {
			t.Errorf("%s exported as %d streams, want 1", name, streams[name])
		}
		got := map[string]int{}
		for _, w := range workloads[name] {
			got[w]++
		}
		// The default registry may hold workloads of other tests; each workload must be reported exactly once.
		if got[first] != 1 || got[second] != 1 {
			t.Errorf("%s points = %v, want one each for %q and %q", name, workloads[name], first, second)
		}
		for w, n := range got {
			if n != 1 {
				t.Errorf("%s reports %q %d times", name, w, n)
			}
		}
	}
	for name, n := range streams {
		if n != 1 {
			t.Errorf("%s exported as %d streams", name, n)
		}
	}
}

// OpenTelemetry SDKs may keep only the first instrument registered under a name on a meter. Every registry on a
// provider must still report its own workloads, in one stream per metric.
func TestTwoRegistriesOnOneProviderBothReportTheirGauges(t *testing.T) {
	reader := sdkmetric.NewManualReader()
	mp := sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader))
	a := runsignal.NewRegistry(runsignal.WithMeterProvider(mp))
	b := runsignal.NewRegistry(runsignal.WithMeterProvider(mp))
	a.MustWorkload("first", runsignal.Schedule("0 2 * * *"), runsignal.MaxDuration(time.Minute))
	b.MustWorkload("second", runsignal.Schedule("0 3 * * *"), runsignal.MaxDuration(time.Minute))

	assertBothWorkloads(t, reader, "first", "second")
}

func TestDefaultAndUserRegistryOnTheGlobalProviderBothReportTheirGauges(t *testing.T) {
	previous := otel.GetMeterProvider()
	t.Cleanup(func() { otel.SetMeterProvider(previous) })
	reader := sdkmetric.NewManualReader()
	otel.SetMeterProvider(sdkmetric.NewMeterProvider(sdkmetric.WithReader(reader)))

	runsignal.Default.MustWorkload("shared-provider-default", runsignal.Schedule("0 2 * * *"), runsignal.MaxDuration(time.Minute))
	user := runsignal.NewRegistry()
	user.MustWorkload("shared-provider-user", runsignal.Schedule("0 3 * * *"), runsignal.MaxDuration(time.Minute))

	assertBothWorkloads(t, reader, "shared-provider-default", "shared-provider-user")
}
