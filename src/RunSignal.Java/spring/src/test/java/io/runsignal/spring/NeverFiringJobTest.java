package io.runsignal.spring;

import static org.assertj.core.api.Assertions.assertThat;

import io.opentelemetry.api.OpenTelemetry;
import io.opentelemetry.api.common.AttributeKey;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.data.MetricData;
import io.opentelemetry.sdk.testing.exporter.InMemoryMetricReader;
import io.runsignal.RunSignal;
import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.SpringBootConfiguration;
import org.springframework.boot.autoconfigure.EnableAutoConfiguration;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.annotation.Bean;
import org.springframework.scheduling.annotation.EnableScheduling;
import org.springframework.scheduling.annotation.Scheduled;

/** An application whose only job never fires still reports the gauges its missed and hung alerts watch. */
@SpringBootTest(classes = NeverFiringJobTest.App.class, properties = "spring.batch.job.enabled=false")
class NeverFiringJobTest {
  @SpringBootConfiguration
  @EnableAutoConfiguration
  @EnableScheduling
  static class App {
    @Bean
    InMemoryMetricReader reader() {
      return InMemoryMetricReader.createDelta();
    }

    @Bean
    OpenTelemetry openTelemetry(InMemoryMetricReader reader) {
      return OpenTelemetrySdk.builder().setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build()).build();
    }

    @Bean
    Jobs jobs() {
      return new Jobs();
    }
  }

  static final class Jobs {
    @Scheduled(cron = "0 0 3 1 1 *") // once a year: never during the test
    @RunSignal(value = "yearly-archive", maxDuration = "2h")
    void archive() {}
  }

  @Autowired
  InMemoryMetricReader reader;

  @Test
  void aScheduledJobThatNeverRanReportsSinceSuccessAndActiveRunAge() {
    Map<String, Double> gauges = new HashMap<>();
    for (MetricData metric : reader.collectAllMetrics()) {
      if (metric.getType() == io.opentelemetry.sdk.metrics.data.MetricDataType.DOUBLE_GAUGE) {
        metric.getDoubleGaugeData().getPoints().forEach(p ->
            gauges.put(metric.getName() + " " + p.getAttributes().get(AttributeKey.stringKey("runsignal.workload")), p.getValue()));
      }
    }
    assertThat(gauges).containsKey("runsignal.workload.since_success yearly-archive")
        .containsEntry("runsignal.workload.active_run_age yearly-archive", 0.0);
  }
}
