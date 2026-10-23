package runsignal

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// Severity is how urgent a workload's alerts are; it routes notifications.
type Severity int

const (
	// Warning needs attention during working hours. It is the default.
	Warning Severity = iota
	// Info is worth knowing about; no action needed now.
	Info
	// Critical needs attention now, for example by paging on-call.
	Critical
)

// String returns "info", "warning" or "critical".
func (s Severity) String() string {
	switch s {
	case Info:
		return "info"
	case Critical:
		return "critical"
	default:
		return "warning"
	}
}

// MarshalJSON writes the severity as the manifest spells it.
func (s Severity) MarshalJSON() ([]byte, error) { return json.Marshal(s.String()) }

// UnmarshalJSON reads "info", "warning" or "critical".
func (s *Severity) UnmarshalJSON(data []byte) error {
	var text string
	if err := json.Unmarshal(data, &text); err != nil {
		return err
	}
	switch strings.ToLower(text) {
	case "info":
		*s = Info
	case "warning":
		*s = Warning
	case "critical":
		*s = Critical
	default:
		return fmt.Errorf("runsignal: unknown severity %q", text)
	}
	return nil
}

// Expectations describe what a workload is expected to do. They define the alerts RunSignal provisions and do
// not change how signals are recorded.
type Expectations struct {
	// Schedule is a cron expression for when the workload should run. Produces the missed alert.
	Schedule string
	// MaxDuration is the longest acceptable run. Produces the overrun alert.
	MaxDuration time.Duration
	// ExpectEvery is the longest acceptable gap between heartbeats. Produces the stalled alert.
	ExpectEvery time.Duration
	// MinProcessed is the fewest items a run should process. Produces the low-output alert.
	MinProcessed int64
	// FailureRateThreshold is the highest acceptable share (0 to 1) of failed runs or items. Produces the
	// failure-rate alert.
	FailureRateThreshold float64
	// Severity is how urgent the workload's alerts are.
	Severity Severity
}

// Option sets one of a workload's expectations.
type Option func(*Expectations)

// Schedule sets the cron expression the workload should run on.
func Schedule(cron string) Option { return func(e *Expectations) { e.Schedule = cron } }

// MaxDuration sets the longest acceptable run.
func MaxDuration(d time.Duration) Option { return func(e *Expectations) { e.MaxDuration = d } }

// ExpectEvery sets the longest acceptable gap between heartbeats.
func ExpectEvery(d time.Duration) Option { return func(e *Expectations) { e.ExpectEvery = d } }

// MinProcessed sets the fewest items a run should process.
func MinProcessed(n int64) Option { return func(e *Expectations) { e.MinProcessed = n } }

// FailureRateThreshold sets the highest acceptable share (0 to 1) of failed runs or items.
func FailureRateThreshold(rate float64) Option {
	return func(e *Expectations) { e.FailureRateThreshold = rate }
}

// WithSeverity sets how urgent the workload's alerts are.
func WithSeverity(s Severity) Option { return func(e *Expectations) { e.Severity = s } }

func (e Expectations) validate() error {
	switch {
	case e.MaxDuration < 0, e.ExpectEvery < 0:
		return fmt.Errorf("runsignal: durations must not be negative")
	case e.MinProcessed < 0:
		return fmt.Errorf("runsignal: MinProcessed must not be negative")
	case e.FailureRateThreshold < 0, e.FailureRateThreshold > 1:
		return fmt.Errorf("runsignal: FailureRateThreshold must be between 0 and 1, got %v", e.FailureRateThreshold)
	}
	return nil
}
