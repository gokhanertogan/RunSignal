package example;

import io.opentelemetry.api.OpenTelemetry;
import io.opentelemetry.api.common.Attributes;
import io.opentelemetry.exporter.logging.LoggingMetricExporter;
import io.opentelemetry.exporter.otlp.metrics.OtlpGrpcMetricExporter;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.export.AggregationTemporalitySelector;
import io.opentelemetry.sdk.metrics.export.MetricExporter;
import io.opentelemetry.sdk.metrics.export.PeriodicMetricReader;
import io.opentelemetry.sdk.resources.Resource;
import io.runsignal.RunSignal;
import io.runsignal.Severity;
import io.runsignal.Workload;
import io.runsignal.WorkloadOptions;
import io.runsignal.WorkloadRegistry;
import java.time.Duration;
import java.util.concurrent.ThreadLocalRandom;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.context.annotation.Bean;
import org.springframework.scheduling.annotation.EnableScheduling;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;

/**
 * Two workloads that HTTP monitoring never sees: a scheduled ledger import and a stock-sync loop. With
 * NEW_RELIC_LICENSE_KEY set, signals go to New Relic over OTLP (NEW_RELIC_REGION=EU for the EU data center);
 * otherwise metrics are logged. Run: RUNSIGNAL_MANIFEST_PATH=runsignal.json mvn spring-boot:run
 */
@SpringBootApplication
@EnableScheduling
public class Worker {
  public static void main(String[] args) {
    SpringApplication.run(Worker.class, args);
  }

  @Bean(destroyMethod = "close")
  OpenTelemetrySdk openTelemetry() {
    String licenseKey = System.getenv("NEW_RELIC_LICENSE_KEY");
    MetricExporter exporter = licenseKey == null
        ? LoggingMetricExporter.create()
        // Delta temporality is required: with cumulative, a stopped workload never looks stopped.
        : OtlpGrpcMetricExporter.builder()
            .setEndpoint("EU".equalsIgnoreCase(System.getenv("NEW_RELIC_REGION")) ? "https://otlp.eu01.nr-data.net:4317" : "https://otlp.nr-data.net:4317")
            .addHeader("api-key", licenseKey)
            .setAggregationTemporalitySelector(AggregationTemporalitySelector.deltaPreferred())
            .build();
    return OpenTelemetrySdk.builder()
        .setMeterProvider(SdkMeterProvider.builder()
            .setResource(Resource.getDefault().merge(Resource.create(Attributes.of(
                io.opentelemetry.api.common.AttributeKey.stringKey("service.name"), "runsignal-java-sample"))))
            .registerMetricReader(PeriodicMetricReader.builder(exporter).setInterval(Duration.ofSeconds(30)).build())
            .build())
        .build();
  }

  @Component
  static class Jobs {
    private final Workload stock;

    Jobs(WorkloadRegistry registry) {
      this.stock = registry.workload("stock-sync", WorkloadOptions.builder().expectEvery("5m").failureRateThreshold(0.2).build());
    }

    @Scheduled(cron = "0 * * * * *")
    @RunSignal(value = "ledger-import", maxDuration = "30s", severity = Severity.CRITICAL)
    void importLedger() throws InterruptedException {
      Thread.sleep(100 + ThreadLocalRandom.current().nextInt(700));
      if (ThreadLocalRandom.current().nextDouble() < 0.2) {
        throw new IllegalStateException("ledger API did not answer");
      }
    }

    @Scheduled(fixedDelay = 2000)
    void syncStock() {
      stock.processed(ThreadLocalRandom.current().nextInt(50));
      if (ThreadLocalRandom.current().nextDouble() < 0.1) {
        stock.itemFailed();
      }
      stock.beat();
    }
  }
}
