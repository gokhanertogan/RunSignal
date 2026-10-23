// Command worker runs two workloads that HTTP monitoring never sees, a scheduled price import and a stock-sync
// loop, and exports their signals. With NEW_RELIC_LICENSE_KEY set it sends them to New Relic over OTLP
// (NEW_RELIC_REGION=EU for the EU data center); otherwise it prints metrics to stdout.
//
//	RUNSIGNAL_MANIFEST_PATH=runsignal.json go run .
package main

import (
	"context"
	"errors"
	"log"
	"math/rand/v2"
	"os"
	"os/signal"
	"strings"
	"time"

	"github.com/robfig/cron/v3"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetricgrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/exporters/stdout/stdoutmetric"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.26.0"

	runsignal "github.com/gokhanertogan/RunSignal/go"
	"github.com/gokhanertogan/RunSignal/go/rscron"
)

const service = "runsignal-go-sample"

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	shutdown, err := setupOpenTelemetry(ctx)
	if err != nil {
		log.Fatal(err)
	}
	defer func() { _ = shutdown(context.Background()) }()

	// Created after the providers are set, but the global providers would also pick them up later.
	registry := runsignal.Default

	c := cron.New()
	if _, err := rscron.AddFunc(c, registry, "price-import", "*/1 * * * *", importPrices,
		runsignal.MaxDuration(30*time.Second), runsignal.WithSeverity(runsignal.Critical)); err != nil {
		log.Fatal(err)
	}
	stock := registry.MustWorkload("stock-sync", runsignal.ExpectEvery(5*time.Minute), runsignal.FailureRateThreshold(0.2))

	if path, err := registry.WriteManifestFromEnv(service); err != nil {
		log.Fatal(err)
	} else if path != "" {
		log.Printf("wrote the RunSignal manifest to %s", path)
	}

	c.Start()
	defer c.Stop()

	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			stock.Processed(ctx, rand.Int64N(50))
			if rand.Float64() < 0.1 {
				stock.ItemFailed(ctx, 1)
			}
			stock.Beat(ctx)
		}
	}
}

func importPrices(ctx context.Context) error {
	time.Sleep(time.Duration(100+rand.IntN(700)) * time.Millisecond)
	if rand.Float64() < 0.2 {
		return errors.New("supplier API did not answer")
	}
	runsignal.Default.MustWorkload("price-import").Processed(ctx, 1+rand.Int64N(20))
	return nil
}

func setupOpenTelemetry(ctx context.Context) (func(context.Context) error, error) {
	res := resource.NewSchemaless(semconv.ServiceName(service))
	licenseKey := os.Getenv("NEW_RELIC_LICENSE_KEY")
	if licenseKey == "" {
		exporter, err := stdoutmetric.New()
		if err != nil {
			return nil, err
		}
		mp := sdkmetric.NewMeterProvider(sdkmetric.WithResource(res),
			sdkmetric.WithReader(sdkmetric.NewPeriodicReader(exporter, sdkmetric.WithInterval(10*time.Second))))
		otel.SetMeterProvider(mp)
		return mp.Shutdown, nil
	}

	endpoint := "otlp.nr-data.net:4317"
	if strings.EqualFold(os.Getenv("NEW_RELIC_REGION"), "EU") {
		endpoint = "otlp.eu01.nr-data.net:4317"
	}
	headers := map[string]string{"api-key": licenseKey}

	// Delta temporality is required: with cumulative, a stopped workload never looks stopped.
	metrics, err := otlpmetricgrpc.New(ctx, otlpmetricgrpc.WithEndpoint(endpoint), otlpmetricgrpc.WithHeaders(headers),
		otlpmetricgrpc.WithTemporalitySelector(sdkmetric.DeltaTemporalitySelector))
	if err != nil {
		return nil, err
	}
	traces, err := otlptracegrpc.New(ctx, otlptracegrpc.WithEndpoint(endpoint), otlptracegrpc.WithHeaders(headers))
	if err != nil {
		return nil, err
	}

	mp := sdkmetric.NewMeterProvider(sdkmetric.WithResource(res),
		sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metrics, sdkmetric.WithInterval(30*time.Second))))
	tp := sdktrace.NewTracerProvider(sdktrace.WithResource(res), sdktrace.WithBatcher(traces))
	otel.SetMeterProvider(mp)
	otel.SetTracerProvider(tp)
	return func(ctx context.Context) error { return errors.Join(mp.Shutdown(ctx), tp.Shutdown(ctx)) }, nil
}
