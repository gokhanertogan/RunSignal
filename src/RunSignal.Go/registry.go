package runsignal

import (
	"context"
	"fmt"
	"sort"
	"sync"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

// Registry creates and holds the workloads of an application and the instruments they record on.
type Registry struct {
	mu        sync.RWMutex
	workloads map[string]*Workload

	base       time.Time
	tracer     trace.Tracer
	runs       metric.Int64Counter
	duration   metric.Float64Histogram
	items      metric.Int64Counter
	heartbeats metric.Int64Counter
}

// RegistryOption configures a Registry.
type RegistryOption func(*registryConfig)

type registryConfig struct {
	meterProvider  metric.MeterProvider
	tracerProvider trace.TracerProvider
	noTracing      bool
}

// WithMeterProvider records metrics on mp instead of the global meter provider.
func WithMeterProvider(mp metric.MeterProvider) RegistryOption {
	return func(c *registryConfig) { c.meterProvider = mp }
}

// WithTracerProvider records run spans on tp instead of the global tracer provider.
func WithTracerProvider(tp trace.TracerProvider) RegistryOption {
	return func(c *registryConfig) { c.tracerProvider = tp }
}

// WithoutTracing records no span per run. Starting a run then allocates nothing, which matters for workloads
// that run thousands of times per second, such as per-message consumers. Metrics are unaffected.
func WithoutTracing() RegistryOption {
	return func(c *registryConfig) { c.noTracing = true }
}

// NewRegistry creates a registry. By default it records on the global OpenTelemetry providers, so it works
// whether the SDK is configured before or after the registry is created.
func NewRegistry(opts ...RegistryOption) *Registry {
	c := registryConfig{}
	for _, opt := range opts {
		opt(&c)
	}
	if c.meterProvider == nil {
		c.meterProvider = otel.GetMeterProvider()
	}
	if c.tracerProvider == nil {
		c.tracerProvider = otel.GetTracerProvider()
	}

	meter := c.meterProvider.Meter(ScopeName, metric.WithInstrumentationVersion(Version))
	r := &Registry{
		workloads: map[string]*Workload{},
		base:      time.Now(),
	}
	if !c.noTracing {
		r.tracer = c.tracerProvider.Tracer(ScopeName, trace.WithInstrumentationVersion(Version))
	}

	// Instrument creation only fails for invalid names or options, which are constants here.
	r.runs, _ = meter.Int64Counter(MetricRuns, metric.WithUnit("{run}"), metric.WithDescription("Completed runs of a workload."))
	r.duration, _ = meter.Float64Histogram(MetricDuration, metric.WithUnit("s"), metric.WithDescription("Duration of workload runs."),
		metric.WithExplicitBucketBoundaries(DurationBuckets...))
	r.items, _ = meter.Int64Counter(MetricItems, metric.WithUnit("{item}"), metric.WithDescription("Items handled by workload runs."))
	r.heartbeats, _ = meter.Int64Counter(MetricHeartbeats, metric.WithUnit("{heartbeat}"), metric.WithDescription("Heartbeats of long-running workloads."))
	sinceSuccess, _ := meter.Float64ObservableGauge(MetricSinceSuccess, metric.WithUnit("s"),
		metric.WithDescription("Seconds since the last successful run of a workload, or since it was registered."))
	sinceHeartbeat, _ := meter.Float64ObservableGauge(MetricSinceHeartbeat, metric.WithUnit("s"),
		metric.WithDescription("Seconds since the last heartbeat of a workload, or since it was registered."))

	_, _ = meter.RegisterCallback(func(_ context.Context, o metric.Observer) error {
		now := r.now()
		for _, w := range r.Workloads() {
			if seconds, ok := w.secondsSinceSuccess(now); ok {
				o.ObserveFloat64(sinceSuccess, seconds, w.nameObserve...)
			}
			if seconds, ok := w.secondsSinceHeartbeat(now); ok {
				o.ObserveFloat64(sinceHeartbeat, seconds, w.nameObserve...)
			}
		}
		return nil
	}, sinceSuccess, sinceHeartbeat)
	return r
}

// Default is a process-wide registry on the global OpenTelemetry providers.
var Default = NewRegistry()

// Workload returns the workload called name, creating it with opts on first use. Call it once and keep the
// result. Passing options for a workload that already exists is an error, so expectations are declared in
// exactly one place.
func (r *Registry) Workload(name string, opts ...Option) (*Workload, error) {
	r.mu.RLock()
	existing, ok := r.workloads[name]
	r.mu.RUnlock()
	if ok {
		if len(opts) > 0 {
			return nil, fmt.Errorf("runsignal: workload %q is already registered; set its options only where it is first created", name)
		}
		return existing, nil
	}
	w, created, err := r.getOrAdd(name, opts)
	if err == nil && !created && len(opts) > 0 {
		return nil, fmt.Errorf("runsignal: workload %q is already registered; set its options only where it is first created", name)
	}
	return w, err
}

// MustWorkload is like Workload but panics on an invalid name or conflicting options. Use it for package-level
// workloads whose names are constants.
func (r *Registry) MustWorkload(name string, opts ...Option) *Workload {
	w, err := r.Workload(name, opts...)
	if err != nil {
		panic(err)
	}
	return w
}

// GetOrAdd returns the workload called name, creating it with opts if it does not exist yet. Unlike Workload,
// an existing workload is returned as is: options declared elsewhere take precedence. Scheduler integrations use
// it to name workloads after jobs.
func (r *Registry) GetOrAdd(name string, opts ...Option) (*Workload, error) {
	w, _, err := r.getOrAdd(name, opts)
	return w, err
}

func (r *Registry) getOrAdd(name string, opts []Option) (*Workload, bool, error) {
	if err := validateName(name); err != nil {
		return nil, false, err
	}
	e := Expectations{}
	for _, opt := range opts {
		opt(&e)
	}
	if err := e.validate(); err != nil {
		return nil, false, err
	}

	r.mu.Lock()
	defer r.mu.Unlock()
	if existing, ok := r.workloads[name]; ok {
		return existing, false, nil
	}
	w := newWorkload(r, name, e)
	r.workloads[name] = w
	return w, true, nil
}

// Workloads returns every workload created so far, sorted by name.
func (r *Registry) Workloads() []*Workload {
	r.mu.RLock()
	list := make([]*Workload, 0, len(r.workloads))
	for _, w := range r.workloads {
		list = append(list, w)
	}
	r.mu.RUnlock()
	sort.Slice(list, func(i, j int) bool { return list[i].name < list[j].name })
	return list
}

// now returns monotonic nanoseconds since the registry was created.
func (r *Registry) now() int64 { return int64(time.Since(r.base)) }
