package io.runsignal;

import static org.assertj.core.api.Assertions.assertThat;

import io.opentelemetry.api.GlobalOpenTelemetry;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.data.MetricData;
import io.opentelemetry.sdk.testing.exporter.InMemoryMetricReader;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.logging.Handler;
import java.util.logging.Level;
import java.util.logging.LogRecord;
import java.util.logging.Logger;
import org.junit.jupiter.api.Test;

/**
 * OpenTelemetry SDKs may keep only the first instrument registered under a name, or report a duplicate as a
 * second, conflicting stream. Every registry recording on one provider must still report its own workloads, in
 * exactly one stream per metric.
 */
class SharedProviderTest {
  private static final WorkloadOptions EXPECTATIONS =
      WorkloadOptions.builder().schedule("0 2 * * *").maxDuration("10m").build();

  @Test
  void twoRegistriesOnOneProviderBothReportTheirGauges() {
    InMemoryMetricReader reader = InMemoryMetricReader.create();
    OpenTelemetrySdk sdk = OpenTelemetrySdk.builder()
        .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build()).build();
    List<LogRecord> warnings = captureWarnings(() -> {
      new WorkloadRegistry(sdk).workload("first", EXPECTATIONS);
      new WorkloadRegistry(sdk, false).workload("second", EXPECTATIONS);
      assertBothWorkloads(reader.collectAllMetrics(), "first", "second");
    });
    assertThat(warnings).isEmpty();
  }

  @Test
  void theGlobalRegistryAndAUserRegistryOnTheGlobalProviderBothReportTheirGauges() {
    GlobalOpenTelemetry.resetForTest();
    WorkloadRegistry.global().resetForTest(); // other tests may have bound it to an earlier GlobalOpenTelemetry
    try {
      InMemoryMetricReader reader = InMemoryMetricReader.create();
      GlobalOpenTelemetry.set(OpenTelemetrySdk.builder()
          .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build()).build());
      List<LogRecord> warnings = captureWarnings(() -> {
        WorkloadRegistry.global().workload("shared-global", EXPECTATIONS);
        new WorkloadRegistry(GlobalOpenTelemetry.get()).workload("shared-user", EXPECTATIONS);
        assertBothWorkloads(reader.collectAllMetrics(), "shared-global", "shared-user");
      });
      assertThat(warnings).isEmpty();
    } finally {
      GlobalOpenTelemetry.resetForTest();
      WorkloadRegistry.global().resetForTest();
    }
  }

  private static void assertBothWorkloads(Collection<MetricData> metrics, String first, String second) {
    for (String name : List.of(RunSignalTelemetry.METRIC_SINCE_SUCCESS, RunSignalTelemetry.METRIC_ACTIVE_RUN_AGE)) {
      List<MetricData> streams = metrics.stream().filter(m -> m.getName().equals(name)).toList();
      assertThat(streams).as(name + " streams").hasSize(1);
      List<String> workloads = new ArrayList<>();
      streams.get(0).getDoubleGaugeData().getPoints().forEach(p -> workloads.add(p.getAttributes().get(Workload.WORKLOAD)));
      assertThat(workloads).as(name + " workloads").containsExactlyInAnyOrder(first, second);
    }
    assertThat(metrics.stream().map(MetricData::getName).toList()).doesNotHaveDuplicates();
  }

  /** Runs {@code body} and returns the warnings OpenTelemetry logged meanwhile, such as duplicate metric definitions. */
  private static List<LogRecord> captureWarnings(Runnable body) {
    Logger logger = Logger.getLogger("io.opentelemetry");
    List<LogRecord> records = new ArrayList<>();
    Handler handler = new Handler() {
      @Override public void publish(LogRecord record) {
        if (record.getLevel().intValue() >= Level.WARNING.intValue()) {
          records.add(record);
        }
      }
      @Override public void flush() {}
      @Override public void close() {}
    };
    logger.addHandler(handler);
    try {
      body.run();
    } finally {
      logger.removeHandler(handler);
    }
    return records;
  }
}
