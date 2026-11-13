package runsignal

import (
	"encoding/json"
	"fmt"
	"net/url"
	"strings"
	"time"
	"unicode/utf8"
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
	// Owner is who answers the workload's alerts: a team or person, such as "billing-team". Added to alert
	// descriptions and, on Prometheus rules, as the owner label.
	Owner string
	// RunbookURL is an absolute http or https URL of what to do when an alert fires. Added to alert
	// descriptions and, on Prometheus rules, as the runbook_url annotation.
	RunbookURL string

	// Set by Owner and RunbookURL, so an explicit empty value is rejected rather than read as unset.
	ownerSet, runbookURLSet bool
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

// Owner sets who answers the workload's alerts: a team or person, such as "billing-team". It must not be blank
// and is at most 128 characters.
func Owner(owner string) Option { return func(e *Expectations) { e.Owner = owner; e.ownerSet = true } }

// RunbookURL sets the absolute http or https URL of what to do when an alert fires.
func RunbookURL(runbookURL string) Option {
	return func(e *Expectations) { e.RunbookURL = runbookURL; e.runbookURLSet = true }
}

const maxOwnerLength = 128

func (e Expectations) validate() error {
	switch {
	case e.MaxDuration < 0, e.ExpectEvery < 0:
		return fmt.Errorf("runsignal: durations must not be negative")
	case e.MinProcessed < 0:
		return fmt.Errorf("runsignal: MinProcessed must not be negative")
	case e.FailureRateThreshold < 0, e.FailureRateThreshold > 1:
		return fmt.Errorf("runsignal: FailureRateThreshold must be between 0 and 1, got %v", e.FailureRateThreshold)
	case (e.ownerSet || e.Owner != "") && strings.TrimSpace(e.Owner) == "":
		return fmt.Errorf("runsignal: Owner must not be blank")
	case utf8.RuneCountInString(e.Owner) > maxOwnerLength:
		return fmt.Errorf("runsignal: Owner must be at most %d characters, got %d", maxOwnerLength, utf8.RuneCountInString(e.Owner))
	case (e.runbookURLSet || e.RunbookURL != "") && !isHTTPURL(e.RunbookURL):
		return fmt.Errorf("runsignal: RunbookURL must be an absolute http or https URL, got %q", e.RunbookURL)
	}
	return nil
}

func isHTTPURL(raw string) bool {
	if !strings.HasPrefix(raw, "http://") && !strings.HasPrefix(raw, "https://") {
		return false
	}
	u, err := url.Parse(raw)
	return err == nil && u.Hostname() != "" && !strings.ContainsAny(raw, " \t\r\n")
}
