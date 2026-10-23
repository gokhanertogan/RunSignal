package io.runsignal;

import static org.assertj.core.api.Assertions.assertThat;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.data.MetricData;
import io.opentelemetry.sdk.testing.exporter.InMemoryMetricReader;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import org.junit.jupiter.api.Test;

/** spec/conformance/signals.json: every SDK runs the same scenario and must emit exactly the same metrics. */
class ConformanceTest {
  private static final ObjectMapper JSON = new ObjectMapper().configure(SerializationFeature.ORDER_MAP_ENTRIES_BY_KEYS, true);
  private static final JsonNodeFactory NODES = JsonNodeFactory.instance;

  @Test
  void scenarioProducesExactlyTheExpectedMetrics() throws Exception {
    JsonNode spec = JSON.readTree(Path.of("../../../spec/conformance/signals.json").toFile());
    InMemoryMetricReader reader = InMemoryMetricReader.create();
    OpenTelemetrySdk sdk = OpenTelemetrySdk.builder()
        .setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build())
        .build();
    WorkloadRegistry registry = new WorkloadRegistry(sdk, false);

    for (JsonNode w : spec.get("workloads")) {
      WorkloadOptions.Builder o = WorkloadOptions.builder();
      if (w.has("schedule")) o.schedule(w.get("schedule").asText());
      if (w.has("maxDurationSeconds")) o.maxDuration(Duration.ofMillis(Math.round(w.get("maxDurationSeconds").asDouble() * 1000)));
      if (w.has("expectEverySeconds")) o.expectEvery(Duration.ofMillis(Math.round(w.get("expectEverySeconds").asDouble() * 1000)));
      if (w.has("minProcessed")) o.minProcessed(w.get("minProcessed").asLong());
      if (w.has("failureRateThreshold")) o.failureRateThreshold(w.get("failureRateThreshold").asDouble());
      if (w.has("severity")) o.severity(Severity.valueOf(w.get("severity").asText().toUpperCase(java.util.Locale.ROOT)));
      registry.workload(w.get("name").asText(), o.build());
    }

    for (JsonNode a : spec.get("actions")) {
      Workload w = registry.workload(a.get("workload").asText());
      switch (a.get("action").asText()) {
        case "succeed" -> w.start().succeed();
        case "failCode" -> w.start().failCode(a.get("code").asText(), null);
        case "cancel" -> w.start().cancel();
        case "processed" -> w.processed(a.get("count").asLong());
        case "itemFailed" -> w.itemFailed(a.get("count").asLong());
        case "beat" -> w.beat();
        default -> throw new IllegalArgumentException("unknown action " + a.get("action"));
      }
    }

    ArrayNode got = NODES.arrayNode();
    for (MetricData metric : reader.collectAllMetrics()) {
      ObjectNode m = NODES.objectNode().put("name", metric.getName()).put("unit", metric.getUnit());
      ArrayNode points = NODES.arrayNode();
      switch (metric.getType()) {
        case LONG_SUM -> {
          m.put("type", "counter");
          metric.getLongSumData().getPoints().forEach(p -> points.add(point(p.getAttributes().asMap()).put("value", (double) p.getValue())));
        }
        case HISTOGRAM -> {
          m.put("type", "histogram");
          metric.getHistogramData().getPoints().forEach(p -> {
            ArrayNode bounds = m.putArray("bounds");
            p.getBoundaries().forEach(bounds::add);
            points.add(point(p.getAttributes().asMap()).put("count", (double) p.getCount()));
          });
        }
        case DOUBLE_GAUGE -> {
          m.put("type", "gauge");
          metric.getDoubleGaugeData().getPoints().forEach(p -> points.add(point(p.getAttributes().asMap())));
        }
        default -> m.put("type", metric.getType().name());
      }
      if (!points.isEmpty()) {
        m.set("points", points);
        got.add(m);
      }
    }

    assertThat(canonical(got)).isEqualTo(canonical(spec.get("expected")));
  }

  private static ObjectNode point(Map<io.opentelemetry.api.common.AttributeKey<?>, Object> attributes) {
    ObjectNode attrs = NODES.objectNode();
    attributes.forEach((k, v) -> attrs.put(k.getKey(), String.valueOf(v)));
    return NODES.objectNode().set("attributes", attrs);
  }

  // Order-insensitive form: metrics sorted by name, points by their attributes, keys sorted, numbers as doubles.
  private static String canonical(JsonNode metrics) throws Exception {
    List<Object> sorted = new ArrayList<>();
    for (JsonNode m : metrics) {
      Map<String, Object> metric = new TreeMap<>();
      m.fieldNames().forEachRemaining(f -> {
        if (!f.equals("points") && !f.equals("bounds")) metric.put(f, m.get(f).asText());
      });
      if (m.has("bounds")) {
        List<Double> bounds = new ArrayList<>();
        m.get("bounds").forEach(b -> bounds.add(b.asDouble()));
        metric.put("bounds", bounds);
      }
      List<Map<String, Object>> points = new ArrayList<>();
      for (JsonNode p : m.get("points")) {
        Map<String, Object> point = new TreeMap<>();
        point.put("attributes", JSON.convertValue(p.get("attributes"), TreeMap.class));
        if (p.has("value")) point.put("value", p.get("value").asDouble());
        if (p.has("count")) point.put("count", p.get("count").asDouble());
        points.add(point);
      }
      points.sort(Comparator.comparing(p -> String.valueOf(p.get("attributes"))));
      metric.put("points", points);
      sorted.add(metric);
    }
    sorted.sort(Comparator.comparing(m -> String.valueOf(((Map<?, ?>) m).get("name"))));
    return JSON.writerWithDefaultPrettyPrinter().writeValueAsString(sorted);
  }
}
