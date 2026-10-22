// Package rscron runs github.com/robfig/cron/v3 jobs as RunSignal workloads: every run is recorded, and the
// job's cron spec becomes the workload's schedule, so a job that never fires gets a missed alert.
//
//	c := cron.New()
//	_, err := rscron.AddFunc(c, runsignal.Default, "nightly-invoice", "0 2 * * *",
//		func(ctx context.Context) error { return generateInvoices(ctx) },
//		runsignal.MaxDuration(30*time.Minute))
//	c.Start()
package rscron

import (
	"context"
	"strings"

	"github.com/robfig/cron/v3"

	runsignal "github.com/gokhanertogan/RunSignal/src/RunSignal.Go"
)

// AddFunc schedules fn on c under spec and records each run as a run of the workload called name. The
// workload is created with spec as its schedule and with opts; a workload already declared elsewhere keeps its
// own options. fn's errors are recorded as failed runs; the scheduler ignores them, as it does for any job.
// Descriptors such as "@every 5m" cannot be read as a cron schedule, so they produce no missed alert.
func AddFunc(c *cron.Cron, registry *runsignal.Registry, name, spec string, fn func(ctx context.Context) error, opts ...runsignal.Option) (cron.EntryID, error) {
	if !strings.HasPrefix(spec, "@") {
		opts = append([]runsignal.Option{runsignal.Schedule(spec)}, opts...)
	}
	workload, err := registry.GetOrAdd(name, opts...)
	if err != nil {
		return 0, err
	}
	return c.AddFunc(spec, func() {
		_ = workload.Run(context.Background(), fn)
	})
}

// Wrap returns a cron.JobWrapper that records every job it wraps as a run of the workload called name, for use
// with cron.WithChain or cron.NewChain. The job cannot report errors, so a run fails only when the job panics;
// use AddFunc for jobs that return errors.
func Wrap(registry *runsignal.Registry, name string, opts ...runsignal.Option) (cron.JobWrapper, error) {
	workload, err := registry.GetOrAdd(name, opts...)
	if err != nil {
		return nil, err
	}
	return func(job cron.Job) cron.Job {
		return cron.FuncJob(func() {
			_ = workload.Run(context.Background(), func(context.Context) error {
				job.Run()
				return nil
			})
		})
	}, nil
}
