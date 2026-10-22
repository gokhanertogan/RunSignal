package io.runsignal.spring;

import static org.assertj.core.api.Assertions.assertThat;

import io.opentelemetry.api.OpenTelemetry;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.data.MetricData;
import io.opentelemetry.sdk.testing.exporter.InMemoryMetricReader;
import io.opentelemetry.sdk.testing.exporter.InMemorySpanExporter;
import io.opentelemetry.sdk.trace.SdkTracerProvider;
import io.opentelemetry.sdk.trace.export.SimpleSpanProcessor;
import io.runsignal.RunSignal;
import io.runsignal.Severity;
import io.runsignal.Workload;
import io.runsignal.WorkloadRegistry;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.SpringBootConfiguration;
import org.springframework.boot.autoconfigure.EnableAutoConfiguration;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.annotation.Bean;
import org.springframework.scheduling.annotation.EnableScheduling;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;

@SpringBootTest(classes = RunSignalAutoConfigurationTest.App.class)
class RunSignalAutoConfigurationTest {
  @TempDir
  static Path dir;

  @DynamicPropertySource
  static void properties(DynamicPropertyRegistry registry) {
    registry.add("spring.application.name", () -> "billing");
    registry.add("runsignal.manifest-path", () -> dir.resolve("runsignal.json").toString());
    registry.add("jobs.cleanup.cron", () -> "@daily");
  }

  @SpringBootConfiguration
  @EnableAutoConfiguration
  @EnableScheduling
  static class App {
    @Bean
    InMemoryMetricReader reader() {
      return InMemoryMetricReader.createDelta();
    }

    @Bean
    InMemorySpanExporter spans() {
      return InMemorySpanExporter.create();
    }

    @Bean
    OpenTelemetry openTelemetry(InMemoryMetricReader reader, InMemorySpanExporter spans) {
      return OpenTelemetrySdk.builder()
          .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build())
          .setTracerProvider(SdkTracerProvider.builder().addSpanProcessor(SimpleSpanProcessor.create(spans)).build())
          .build();
    }

    @Bean
    Jobs jobs() {
      return new Jobs();
    }
  }

  // Final, to show that no proxy is needed.
  static final class Jobs {
    final AtomicInteger calls = new AtomicInteger();

    @Scheduled(fixedDelay = 50)
    void flaky() {
      if (calls.incrementAndGet() == 1) {
        throw new IllegalStateException("supplier down");
      }
    }

    @Scheduled(cron = "0 0 4 * * *")
    @RunSignal(value = "nightly-report", maxDuration = "10m", severity = Severity.CRITICAL)
    void nightly() {}

    @Scheduled(cron = "${jobs.cleanup.cron}")
    void cleanup() {}
  }

  @Autowired
  Jobs jobs;

  @Autowired
  WorkloadRegistry registry;

  @Autowired
  InMemoryMetricReader reader;

  @Autowired
  InMemorySpanExporter spans;

  @Test
  void recordsScheduledRunsDiscoversCronJobsAndWritesTheManifest() throws Exception {
    long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
    while (jobs.calls.get() < 3 && System.nanoTime() < deadline) {
      Thread.sleep(25);
    }

    Map<String, Long> runs = new HashMap<>();
    for (MetricData metric : reader.collectAllMetrics()) {
      if (metric.getName().equals("runsignal.workload.runs")) {
        metric.getLongSumData().getPoints().forEach(p -> runs.merge(
            p.getAttributes().asMap().values().stream().map(String::valueOf).sorted().reduce((a, b) -> a + "|" + b).orElse(""),
            p.getValue(), Long::sum));
      }
    }
    assertThat(runs).containsEntry("failure|java.lang.IllegalStateException|jobs.flaky", 1L);
    assertThat(runs.get("jobs.flaky|success")).isGreaterThanOrEqualTo(1L);
    assertThat(spans.getFinishedSpanItems()).anySatisfy(s -> assertThat(s.getName()).isEqualTo("jobs.flaky"));

    // Discovered before they ever fire, with the resolved cron.
    Workload nightly = registry.workload("nightly-report");
    assertThat(nightly.options().schedule()).contains("0 0 4 * * *");
    assertThat(nightly.options().maxDuration()).contains(Duration.ofMinutes(10));
    assertThat(nightly.options().severity()).isEqualTo(Severity.CRITICAL);
    assertThat(registry.workload("jobs.cleanup").options().schedule()).contains("0 0 0 * * *");

    String manifest = Files.readString(dir.resolve("runsignal.json"));
    assertThat(manifest).contains("\"service\": \"billing\"", "\"name\": \"nightly-report\"", "\"name\": \"jobs.cleanup\"");
  }

  @Test
  void springCronMacrosAndDisabledMarkerAreConverted() {
    assertThat(ScheduledWorkloads.standardCron("@hourly")).isEqualTo("0 0 * * * *");
    assertThat(ScheduledWorkloads.standardCron("@weekly")).isEqualTo("0 0 0 * * 0");
    assertThat(ScheduledWorkloads.standardCron("-")).isNull();
    assertThat(ScheduledWorkloads.standardCron("0 0 9 * * MON-FRI")).isEqualTo("0 0 9 * * MON-FRI");
  }
}
