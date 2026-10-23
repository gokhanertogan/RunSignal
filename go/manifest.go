package runsignal

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Manifest lists every workload of a service and what it is expected to do: the input for alert provisioning
// (runsignal plan / diff / provision). The JSON format is spec/manifest.schema.json, the same in every SDK.
type Manifest struct {
	Version   int             `json:"version"`
	Service   string          `json:"service"`
	Workloads []ManifestEntry `json:"workloads"`
}

// ManifestEntry is one workload in a Manifest. Durations are in seconds so every language reads them the same way.
type ManifestEntry struct {
	Name                 string   `json:"name"`
	Schedule             string   `json:"schedule,omitempty"`
	MaxDurationSeconds   *float64 `json:"maxDurationSeconds,omitempty"`
	ExpectEverySeconds   *float64 `json:"expectEverySeconds,omitempty"`
	MinProcessed         *int64   `json:"minProcessed,omitempty"`
	FailureRateThreshold *float64 `json:"failureRateThreshold,omitempty"`
	Severity             Severity `json:"severity"`
}

// Manifest describes every workload created so far. service must match the OpenTelemetry service.name the
// signals are exported with.
func (r *Registry) Manifest(service string) Manifest {
	m := Manifest{Version: 1, Service: service, Workloads: []ManifestEntry{}}
	for _, w := range r.Workloads() {
		e := w.expectations
		entry := ManifestEntry{Name: w.name, Schedule: e.Schedule, Severity: e.Severity}
		if e.MaxDuration > 0 {
			entry.MaxDurationSeconds = ptr(e.MaxDuration.Seconds())
		}
		if e.ExpectEvery > 0 {
			entry.ExpectEverySeconds = ptr(e.ExpectEvery.Seconds())
		}
		if e.MinProcessed > 0 {
			entry.MinProcessed = ptr(e.MinProcessed)
		}
		if e.FailureRateThreshold > 0 {
			entry.FailureRateThreshold = ptr(e.FailureRateThreshold)
		}
		m.Workloads = append(m.Workloads, entry)
	}
	return m
}

// WriteManifest writes the manifest to path as indented JSON. It writes a temporary file and renames it into
// place, so a reader never sees half a manifest.
func (r *Registry) WriteManifest(path, service string) error {
	data, err := json.MarshalIndent(r.Manifest(service), "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// WriteManifestFromEnv writes the manifest if the RUNSIGNAL_MANIFEST_PATH environment variable is set, so CI
// can ask for a manifest without code changes. Call it once every workload has been registered, for example
// after scheduling your cron jobs. The service name is service, or else OTEL_SERVICE_NAME, or else the
// executable name. It returns the path written, or "" when the variable is not set.
func (r *Registry) WriteManifestFromEnv(service string) (string, error) {
	path := os.Getenv("RUNSIGNAL_MANIFEST_PATH")
	if strings.TrimSpace(path) == "" {
		return "", nil
	}
	if service == "" {
		service = os.Getenv("OTEL_SERVICE_NAME")
	}
	if service == "" {
		service = filepath.Base(os.Args[0])
	}
	if err := r.WriteManifest(path, service); err != nil {
		return "", fmt.Errorf("runsignal: writing the manifest to %s: %w", path, err)
	}
	return path, nil
}

func ptr[T any](v T) *T { return &v }
