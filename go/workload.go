package runsignal

import (
	"context"
	"errors"
	"fmt"
	"sync/atomic"
	"time"

	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

// Workload is a unit of background work, such as a scheduled job, a queue consumer or a sync loop.
// Get one from a Registry once and keep it: every method is safe for concurrent use.
type Workload struct {
	name         string
	expectations Expectations
	registry     *Registry

	// Prebuilt measurement options, so recording does not build attribute sets.
	nameOnly                    []metric.AddOption
	success, failure, cancelled []metric.AddOption
	successRecord               []metric.RecordOption
	failureRecord               []metric.RecordOption
	cancelledRecord             []metric.RecordOption
	processed, failed           []metric.AddOption
	nameObserve                 []metric.ObserveOption
	nameAttribute               attribute.KeyValue

	// Monotonic nanoseconds since the registry was created. Until the first success or heartbeat they hold the
	// time the workload was created, so a workload that never succeeds or never beats still reports a growing age.
	lastSuccess   atomic.Int64
	lastHeartbeat atomic.Int64
	hasStarted    atomic.Bool
	hasBeaten     atomic.Bool
}

func newWorkload(r *Registry, name string, e Expectations) *Workload {
	nameAttr := attribute.String(AttributeWorkload, name)
	addSet := func(kvs ...attribute.KeyValue) []metric.AddOption {
		set := attribute.NewSet(append([]attribute.KeyValue{nameAttr}, kvs...)...)
		return []metric.AddOption{metric.WithAttributeSet(set)}
	}
	recordSet := func(outcome string) []metric.RecordOption {
		return []metric.RecordOption{metric.WithAttributeSet(attribute.NewSet(nameAttr, attribute.String(AttributeOutcome, outcome)))}
	}

	w := &Workload{
		name:            name,
		expectations:    e,
		registry:        r,
		nameAttribute:   nameAttr,
		nameOnly:        addSet(),
		success:         addSet(attribute.String(AttributeOutcome, "success")),
		failure:         addSet(attribute.String(AttributeOutcome, "failure")),
		cancelled:       addSet(attribute.String(AttributeOutcome, "cancelled")),
		successRecord:   recordSet("success"),
		failureRecord:   recordSet("failure"),
		cancelledRecord: recordSet("cancelled"),
		processed:       addSet(attribute.String(AttributeItemResult, "processed")),
		failed:          addSet(attribute.String(AttributeItemResult, "failed")),
		nameObserve:     []metric.ObserveOption{metric.WithAttributeSet(attribute.NewSet(nameAttr))},
	}
	now := r.now()
	w.lastSuccess.Store(now)
	w.lastHeartbeat.Store(now)
	return w
}

// Name returns the workload's stable name, such as "nightly-invoice".
func (w *Workload) Name() string { return w.name }

// Expectations returns what the workload is expected to do.
func (w *Workload) Expectations() Expectations { return w.expectations }

var spanStartOptions = []trace.SpanStartOption{trace.WithSpanKind(trace.SpanKindInternal)}

// Start starts a run and returns a context carrying its span. End the run with exactly one call to Succeed,
// Fail, FailCode or Cancel. Prefer Run, which ends the run for you.
func (w *Workload) Start(ctx context.Context) (context.Context, Run) {
	var span trace.Span
	if w.registry.tracer != nil {
		ctx, span = w.registry.tracer.Start(ctx, w.name, spanStartOptions...)
		if span.IsRecording() {
			span.SetAttributes(w.nameAttribute)
		}
	}
	if !w.hasStarted.Load() {
		w.hasStarted.Store(true)
	}
	return ctx, Run{workload: w, ctx: ctx, start: w.registry.now(), span: span}
}

// Run runs fn as one run of the workload: the run succeeds when fn returns nil, is cancelled when fn returns
// because ctx was cancelled, and fails otherwise. fn's error is returned. A panic in fn fails the run and is
// re-raised.
func (w *Workload) Run(ctx context.Context, fn func(ctx context.Context) error) (err error) {
	runCtx, run := w.Start(ctx)
	defer func() {
		if recovered := recover(); recovered != nil {
			run.Fail(fmt.Errorf("panic: %v", recovered))
			panic(recovered)
		}
	}()

	err = fn(runCtx)
	switch {
	case err == nil:
		run.Succeed()
	case ctx.Err() != nil && errors.Is(err, ctx.Err()):
		run.Cancel()
	default:
		run.Fail(err)
	}
	return err
}

// Processed records n successfully processed items. Recording 0 is useful: it reports that a run did nothing.
func (w *Workload) Processed(ctx context.Context, n int64) {
	if n < 0 {
		return
	}
	w.registry.items.Add(ctx, n, w.processed...)
}

// ItemFailed records n items that could not be processed.
func (w *Workload) ItemFailed(ctx context.Context, n int64) {
	if n < 0 {
		return
	}
	w.registry.items.Add(ctx, n, w.failed...)
}

// Beat records a heartbeat: the workload is alive and making progress.
func (w *Workload) Beat(ctx context.Context) {
	w.lastHeartbeat.Store(w.registry.now())
	if !w.hasBeaten.Load() {
		w.hasBeaten.Store(true)
	}
	w.registry.heartbeats.Add(ctx, 1, w.nameOnly...)
}

// Reported for workloads that are scheduled or have run, so alerts can tell a missed run from a workload that
// simply has no runs.
func (w *Workload) secondsSinceSuccess(now int64) (float64, bool) {
	return time.Duration(now - w.lastSuccess.Load()).Seconds(), w.hasStarted.Load() || w.expectations.Schedule != ""
}

// Reported for workloads that expect heartbeats or have sent one.
func (w *Workload) secondsSinceHeartbeat(now int64) (float64, bool) {
	return time.Duration(now - w.lastHeartbeat.Load()).Seconds(), w.hasBeaten.Load() || w.expectations.ExpectEvery > 0
}

// Run is one run of a Workload, started with Workload.Start. End it with exactly one call to Succeed, Fail,
// FailCode or Cancel. It is a value so that starting a run does not allocate; ending it more than once records
// it more than once. A run that is never ended records nothing, which the backend sees as a missed run.
type Run struct {
	workload *Workload
	ctx      context.Context
	start    int64
	span     trace.Span // nil when tracing is off
}

// Elapsed returns the time since the run started.
func (r Run) Elapsed() time.Duration {
	if r.workload == nil {
		return 0
	}
	return time.Duration(r.workload.registry.now() - r.start)
}

// Succeed ends the run as successful.
func (r Run) Succeed() {
	if r.workload == nil {
		return
	}
	w := r.workload
	now := w.registry.now()
	w.registry.runs.Add(r.ctx, 1, w.success...)
	w.registry.duration.Record(r.ctx, time.Duration(now-r.start).Seconds(), w.successRecord...)
	w.lastSuccess.Store(now)
	if r.span != nil {
		if r.span.IsRecording() {
			r.span.SetAttributes(attribute.String(AttributeOutcome, "success"))
			r.span.SetStatus(codes.Ok, "")
		}
		r.span.End()
	}
}

// ErrorTyper lets an error supply its own low-cardinality error.type, such as "statement_missing".
type ErrorTyper interface {
	ErrorType() string
}

// Fail ends the run as failed because of err. The error.type attribute is err's ErrorType() if it implements
// ErrorTyper, otherwise err's Go type, such as "*fs.PathError". Wrapping with fmt.Errorf("...: %w") is looked
// through, so the type of the wrapped error is used.
func (r Run) Fail(err error) {
	if err == nil {
		err = errors.New("unknown error")
	}
	r.fail(errorType(err), err.Error(), err)
}

// FailCode ends the run as failed without an error value, for example when an expected input file is missing.
// code must be a short, stable value such as "statement_missing"; never put messages or IDs in it.
// description is optional detail, recorded on the span only.
func (r Run) FailCode(code, description string) {
	if code == "" {
		code = "unknown"
	}
	r.fail(code, description, nil)
}

// Cancel ends the run as cancelled, for example on shutdown. Cancelled runs do not count as failures.
func (r Run) Cancel() {
	if r.workload == nil {
		return
	}
	w := r.workload
	w.registry.runs.Add(r.ctx, 1, w.cancelled...)
	w.registry.duration.Record(r.ctx, time.Duration(w.registry.now()-r.start).Seconds(), w.cancelledRecord...)
	if r.span != nil {
		if r.span.IsRecording() {
			r.span.SetAttributes(attribute.String(AttributeOutcome, "cancelled"))
		}
		r.span.End()
	}
}

func (r Run) fail(errorType, description string, err error) {
	if r.workload == nil {
		return
	}
	w := r.workload
	set := attribute.NewSet(w.nameAttribute, attribute.String(AttributeOutcome, "failure"), attribute.String(AttributeErrorType, errorType))
	w.registry.runs.Add(r.ctx, 1, metric.WithAttributeSet(set))
	w.registry.duration.Record(r.ctx, time.Duration(w.registry.now()-r.start).Seconds(), w.failureRecord...)
	if r.span != nil {
		if r.span.IsRecording() {
			r.span.SetAttributes(attribute.String(AttributeOutcome, "failure"), attribute.String(AttributeErrorType, errorType))
			if err != nil {
				r.span.RecordError(err)
			}
			r.span.SetStatus(codes.Error, description)
		}
		r.span.End()
	}
}

func errorType(err error) string {
	var typer ErrorTyper
	if errors.As(err, &typer) {
		return typer.ErrorType()
	}
	for {
		name := fmt.Sprintf("%T", err)
		if name != "*fmt.wrapError" && name != "*fmt.wrapErrors" {
			return name
		}
		inner := errors.Unwrap(err)
		if inner == nil {
			if multi, ok := err.(interface{ Unwrap() []error }); ok && len(multi.Unwrap()) > 0 {
				inner = multi.Unwrap()[0]
			} else {
				return name
			}
		}
		err = inner
	}
}
