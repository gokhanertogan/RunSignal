package io.runsignal;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import io.opentelemetry.api.common.AttributeKey;
import io.opentelemetry.api.trace.Span;
import io.opentelemetry.api.trace.StatusCode;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.data.MetricData;
import io.opentelemetry.sdk.testing.exporter.InMemoryMetricReader;
import io.opentelemetry.sdk.testing.exporter.InMemorySpanExporter;
import io.opentelemetry.sdk.trace.SdkTracerProvider;
import io.opentelemetry.sdk.trace.export.SimpleSpanProcessor;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeoutException;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

class WorkloadTest {
  private InMemoryMetricReader reader;
  private InMemorySpanExporter spans;
  private WorkloadRegistry registry;

  @BeforeEach
  void setUp() {
    reader = InMemoryMetricReader.createDelta();
    spans = InMemorySpanExporter.create();
    OpenTelemetrySdk sdk = OpenTelemetrySdk.builder()
        .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build())
        .setTracerProvider(SdkTracerProvider.builder().addSpanProcessor(SimpleSpanProcessor.create(spans)).build())
        .build();
    registry = new WorkloadRegistry(sdk);
  }

  record Point(double value, Map<String, String> attributes) {}

  private Map<String, List<Point>> collect() {
    Map<String, List<Point>> out = new HashMap<>();
    for (MetricData metric : reader.collectAllMetrics()) {
      List<Point> points = out.computeIfAbsent(metric.getName(), n -> new ArrayList<>());
      switch (metric.getType()) {
        case LONG_SUM -> metric.getLongSumData().getPoints().forEach(p -> points.add(new Point(p.getValue(), attrs(p.getAttributes()))));
        case DOUBLE_GAUGE -> metric.getDoubleGaugeData().getPoints().forEach(p -> points.add(new Point(p.getValue(), attrs(p.getAttributes()))));
        case HISTOGRAM -> metric.getHistogramData().getPoints().forEach(p -> points.add(new Point(p.getSum(), attrs(p.getAttributes()))));
        default -> { }
      }
    }
    return out;
  }

  private static Map<String, String> attrs(io.opentelemetry.api.common.Attributes attributes) {
    Map<String, String> map = new HashMap<>();
    attributes.forEach((k, v) -> map.put(k.getKey(), String.valueOf(v)));
    return map;
  }

  @Test
  void runRecordsOutcomeDurationAndSpan() throws Exception {
    int result = registry.workload("nightly-invoice").call(() -> 42);

    assertThat(result).isEqualTo(42);
    Map<String, List<Point>> m = collect();
    assertThat(m.get(RunSignalTelemetry.METRIC_RUNS)).containsExactly(
        new Point(1, Map.of("runsignal.workload", "nightly-invoice", "runsignal.outcome", "success")));
    assertThat(m.get(RunSignalTelemetry.METRIC_DURATION)).hasSize(1);
    assertThat(spans.getFinishedSpanItems()).singleElement().satisfies(span -> {
      assertThat(span.getName()).isEqualTo("nightly-invoice");
      assertThat(span.getStatus().getStatusCode()).isEqualTo(StatusCode.OK);
    });
  }

  static class StatementMissing extends Exception implements ErrorTyped {
    @Override
    public String errorType() {
      return "statement_missing";
    }
  }

  @Test
  void failureRecordsTheErrorTypeAndRethrows() {
    Map<Exception, String> cases = Map.of(
        new TimeoutException("bank did not answer"), "java.util.concurrent.TimeoutException",
        new ExecutionException(new IllegalStateException("locked")), "java.lang.IllegalStateException",
        new StatementMissing(), "statement_missing");

    cases.forEach((error, expected) -> {
      setUp();
      assertThatThrownBy(() -> registry.workload("import").run(() -> { throw error; })).isSameAs(error);
      assertThat(collect().get(RunSignalTelemetry.METRIC_RUNS)).singleElement().satisfies(p -> {
        assertThat(p.attributes()).containsEntry("runsignal.outcome", "failure").containsEntry("error.type", expected);
      });
    });
  }

  @Test
  void interruptionAndCancellationAreCancelledNotFailed() {
    Workload workload = registry.workload("sync");
    assertThatThrownBy(() -> workload.run(() -> { throw new InterruptedException(); })).isInstanceOf(InterruptedException.class);
    assertThatThrownBy(() -> workload.run(() -> { throw new CancellationException(); })).isInstanceOf(CancellationException.class);

    assertThat(collect().get(RunSignalTelemetry.METRIC_RUNS)).singleElement()
        .satisfies(p -> assertThat(p).isEqualTo(new Point(2, Map.of("runsignal.workload", "sync", "runsignal.outcome", "cancelled"))));
  }

  @Test
  void runIsRecordedOnceAndItsSpanIsCurrentInsideRun() throws Exception {
    WorkloadRun run = registry.workload("once").start();
    run.succeed();
    run.fail(new RuntimeException("late"));
    run.cancel();
    assertThat(collect().get(RunSignalTelemetry.METRIC_RUNS)).hasSize(1);

    String[] current = new String[1];
    registry.workload("nested").run(() -> current[0] = Span.current().getSpanContext().getSpanId());
    assertThat(spans.getFinishedSpanItems()).anySatisfy(s -> assertThat(s.getSpanId()).isEqualTo(current[0]));
  }

  @Test
  void noSpansWhenTracingIsOff() throws Exception {
    OpenTelemetrySdk sdk = OpenTelemetrySdk.builder()
        .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build())
        .setTracerProvider(SdkTracerProvider.builder().addSpanProcessor(SimpleSpanProcessor.create(spans)).build())
        .build();
    new WorkloadRegistry(sdk, false).workload("consumer").run(() -> { });

    assertThat(spans.getFinishedSpanItems()).isEmpty();
    assertThat(collect().get(RunSignalTelemetry.METRIC_RUNS)).hasSize(1);
  }

  @Test
  void itemsHeartbeatsAndAgeGauges() throws Exception {
    Workload scheduled = registry.workload("scheduled", WorkloadOptions.builder().schedule("0 2 * * *").build());
    Workload loop = registry.workload("loop", WorkloadOptions.builder().expectEvery("5m").build());
    registry.workload("bare");

    scheduled.processed(10);
    scheduled.itemFailed(2);
    Thread.sleep(50);
    loop.beat();

    Map<String, List<Point>> m = collect();
    Map<String, Double> items = new HashMap<>();
    m.get(RunSignalTelemetry.METRIC_ITEMS).forEach(p -> items.merge(p.attributes().get("runsignal.item.result"), p.value(), Double::sum));
    assertThat(items).containsEntry("processed", 10.0).containsEntry("failed", 2.0);
    assertThat(m.get(RunSignalTelemetry.METRIC_HEARTBEATS)).hasSize(1);

    // A scheduled workload reports its age before its first run; an unscheduled one does not.
    assertThat(m.get(RunSignalTelemetry.METRIC_SINCE_SUCCESS)).singleElement().satisfies(p -> {
      assertThat(p.attributes()).containsEntry("runsignal.workload", "scheduled");
      assertThat(p.value()).isGreaterThanOrEqualTo(0.04);
    });
    assertThat(m.get(RunSignalTelemetry.METRIC_SINCE_HEARTBEAT)).singleElement()
        .satisfies(p -> assertThat(p.value()).isLessThan(0.04));
  }

  @Test
  void registryValidatesNamesAndKeepsOptionsInOnePlace() {
    for (String bad : List.of("", "Nightly", "has space", "-dash", "order/{id}")) {
      assertThatThrownBy(() -> registry.workload(bad)).isInstanceOf(IllegalArgumentException.class);
    }
    assertThatThrownBy(() -> WorkloadOptions.builder().failureRateThreshold(1.5)).isInstanceOf(IllegalArgumentException.class);
    assertThatThrownBy(() -> WorkloadOptions.builder().maxDuration("soon")).isInstanceOf(IllegalArgumentException.class);

    Workload first = registry.workload("sync", WorkloadOptions.builder().severity(Severity.CRITICAL).build());
    assertThat(registry.workload("sync")).isSameAs(first);
    assertThatThrownBy(() -> registry.workload("sync", WorkloadOptions.NONE)).isInstanceOf(IllegalStateException.class);
    assertThat(registry.getOrAdd("sync", WorkloadOptions.builder().severity(Severity.INFO).build()).options().severity())
        .isEqualTo(Severity.CRITICAL);
  }

  @Test
  void namesMatchTheCrossLanguageVectors() throws IOException {
    String json = Files.readString(Path.of("../../../spec/conformance/workload-names.json"));
    Matcher m = Pattern.compile("\\{ \"input\": \"([^\"]*)\", \"expected\": \"([^\"]*)\" }").matcher(json);
    int count = 0;
    while (m.find()) {
      assertThat(WorkloadNames.toWorkloadName(m.group(1))).as(m.group(1)).isEqualTo(m.group(2));
      count++;
    }
    assertThat(count).isGreaterThanOrEqualTo(10);

    String invalid = json.substring(json.indexOf("\"invalid\""));
    Matcher inputs = Pattern.compile("\"([^\"]*)\"").matcher(invalid.substring(invalid.indexOf('[')));
    while (inputs.find()) {
      String input = inputs.group(1);
      assertThatThrownBy(() -> WorkloadNames.toWorkloadName(input)).as(input).isInstanceOf(IllegalArgumentException.class);
    }
  }

  @Test
  void manifestIsByteForByteTheSameAsTheOtherSdks(@TempDir Path dir) throws IOException {
    registry.workload("stock-sync", WorkloadOptions.builder().expectEvery(Duration.ofMinutes(5)).failureRateThreshold(0.2).build());
    registry.workload("nightly-invoice", WorkloadOptions.builder().schedule("0 2 * * *").maxDuration("30m")
        .minProcessed(100).severity(Severity.CRITICAL).build());

    Path path = dir.resolve("out/manifest.json");
    registry.writeManifest(path, "billing");

    assertThat(Files.readString(path)).isEqualTo("""
        {
          "version": 1,
          "service": "billing",
          "workloads": [
            {
              "name": "nightly-invoice",
              "schedule": "0 2 * * *",
              "maxDurationSeconds": 1800,
              "minProcessed": 100,
              "severity": "critical"
            },
            {
              "name": "stock-sync",
              "expectEverySeconds": 300,
              "failureRateThreshold": 0.2,
              "severity": "warning"
            }
          ]
        }""");
  }

  @Test
  void attributeKeysMatchTheSpec() {
    assertThat(Workload.WORKLOAD).isEqualTo(AttributeKey.stringKey("runsignal.workload"));
    assertThat(Workload.ERROR_TYPE).isEqualTo(AttributeKey.stringKey("error.type"));
  }
}
