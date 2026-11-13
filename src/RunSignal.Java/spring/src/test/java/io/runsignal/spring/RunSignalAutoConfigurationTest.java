package io.runsignal.spring;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

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
    registry.add("runsignal.workloads.weekly-report.owner", () -> "ops-team");
    registry.add("runsignal.workloads.weekly-report.runbook-url", () -> "https://wiki.example.com/weekly");
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
    @RunSignal(value = "nightly-report", maxDuration = "10m", severity = Severity.CRITICAL,
        owner = "billing-team", runbookUrl = "https://wiki.example.com/nightly")
    void nightly() {}

    // The properties take precedence over the annotation.
    @Scheduled(cron = "0 0 5 * * 1")
    @RunSignal(value = "weekly-report", owner = "annotated-team")
    void weekly() {}

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
    Map<String, Double> activeRunAge = new HashMap<>();
    for (MetricData metric : reader.collectAllMetrics()) {
      if (metric.getName().equals("runsignal.workload.active_run_age")) {
        metric.getDoubleGaugeData().getPoints().forEach(p -> activeRunAge.put(
            p.getAttributes().get(io.opentelemetry.api.common.AttributeKey.stringKey("runsignal.workload")), p.getValue()));
      }
      if (metric.getName().equals("runsignal.workload.runs")) {
        metric.getLongSumData().getPoints().forEach(p -> runs.merge(
            p.getAttributes().asMap().values().stream().map(String::valueOf).sorted().reduce((a, b) -> a + "|" + b).orElse(""),
            p.getValue(), Long::sum));
      }
    }
    assertThat(runs).containsEntry("failure|java.lang.IllegalStateException|jobs.flaky", 1L);
    assertThat(runs.get("jobs.flaky|success")).isGreaterThanOrEqualTo(1L);
    assertThat(spans.getFinishedSpanItems()).anySatisfy(s -> assertThat(s.getName()).isEqualTo("jobs.flaky"));
    // Every observed run is ended, so none looks hung; a workload with maxDuration reports 0 before it first runs.
    assertThat(activeRunAge.get("jobs.flaky")).isLessThan(1.0);
    assertThat(activeRunAge).containsEntry("nightly-report", 0.0);

    // Discovered before they ever fire, with the resolved cron.
    Workload nightly = registry.workload("nightly-report");
    assertThat(nightly.options().schedule()).contains("0 0 4 * * *");
    assertThat(nightly.options().maxDuration()).contains(Duration.ofMinutes(10));
    assertThat(nightly.options().severity()).isEqualTo(Severity.CRITICAL);
    assertThat(nightly.options().owner()).contains("billing-team");
    assertThat(nightly.options().runbookUrl()).contains("https://wiki.example.com/nightly");
    Workload weekly = registry.workload("weekly-report");
    assertThat(weekly.options().owner()).contains("ops-team");
    assertThat(weekly.options().runbookUrl()).contains("https://wiki.example.com/weekly");
    assertThat(weekly.options().schedule()).contains("0 0 5 * * 1");
    assertThat(registry.workload("jobs.cleanup").options().schedule()).contains("0 0 0 * * *");
    assertThat(registry.workload("jobs.cleanup").options().owner()).isEmpty();

    String manifest = Files.readString(dir.resolve("runsignal.json"));
    assertThat(manifest).contains("\"service\": \"billing\"", "\"name\": \"nightly-report\"", "\"name\": \"jobs.cleanup\"",
        "\"severity\": \"critical\",\n      \"owner\": \"billing-team\",\n      \"runbookUrl\": \"https://wiki.example.com/nightly\"\n    }");
  }

  @Test
  void invalidOwnerOrRunbookUrlInPropertiesIsRejected() {
    RunSignalProperties.Workload blankOwner = new RunSignalProperties.Workload();
    blankOwner.setOwner(" ");
    assertThatThrownBy(() -> blankOwner.toOptions(null)).isInstanceOf(IllegalArgumentException.class).hasMessageContaining("owner");
    RunSignalProperties.Workload relativeUrl = new RunSignalProperties.Workload();
    relativeUrl.setRunbookUrl("wiki/runbook");
    assertThatThrownBy(() -> relativeUrl.toOptions(null)).isInstanceOf(IllegalArgumentException.class).hasMessageContaining("runbookUrl");
  }

  @Test
  void anObservationStoppedWithoutCompletingIsCancelledAndNoLongerInProgress() throws Exception {
    // Its own registry and reader: the application's delta reader is shared with the other test.
    InMemoryMetricReader reader = InMemoryMetricReader.createDelta();
    WorkloadRegistry registry = new WorkloadRegistry(OpenTelemetrySdk.builder()
        .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build()).build(), false);
    var handler = new RunSignalObservationHandler(new ScheduledWorkloads(registry,
        new org.springframework.mock.env.MockEnvironment(), new RunSignalProperties()));
    var context = new org.springframework.scheduling.support.ScheduledTaskObservationContext(
        jobs, Jobs.class.getDeclaredMethod("cleanup"));
    handler.onStart(context);
    handler.onStop(context); // as Spring does when it cancels a reactive @Scheduled subscription

    Map<String, Long> runs = new HashMap<>();
    double age = Double.NaN;
    for (MetricData metric : reader.collectAllMetrics()) {
      for (var p : metric.getName().equals("runsignal.workload.runs") ? metric.getLongSumData().getPoints() : java.util.List.<io.opentelemetry.sdk.metrics.data.LongPointData>of()) {
        if ("jobs.cleanup".equals(p.getAttributes().get(io.opentelemetry.api.common.AttributeKey.stringKey("runsignal.workload")))) {
          runs.merge(p.getAttributes().get(io.opentelemetry.api.common.AttributeKey.stringKey("runsignal.outcome")), p.getValue(), Long::sum);
        }
      }
      if (metric.getName().equals("runsignal.workload.active_run_age")) {
        for (var p : metric.getDoubleGaugeData().getPoints()) {
          if ("jobs.cleanup".equals(p.getAttributes().get(io.opentelemetry.api.common.AttributeKey.stringKey("runsignal.workload")))) {
            age = p.getValue();
          }
        }
      }
    }
    assertThat(runs).containsExactly(Map.entry("cancelled", 1L));
    assertThat(age).isZero();
  }

  @Test
  void springCronMacrosAndDisabledMarkerAreConverted() {
    assertThat(ScheduledWorkloads.standardCron("@hourly")).isEqualTo("0 0 * * * *");
    assertThat(ScheduledWorkloads.standardCron("@weekly")).isEqualTo("0 0 0 * * 0");
    assertThat(ScheduledWorkloads.standardCron("-")).isNull();
    assertThat(ScheduledWorkloads.standardCron("0 0 9 * * MON-FRI")).isEqualTo("0 0 9 * * MON-FRI");
  }
}
