package io.runsignal;

import java.math.BigDecimal;
import java.util.List;

/** Writes the manifest exactly as the other SDKs do: two-space indentation, nulls left out. */
final class ManifestJson {
  private ManifestJson() {}

  static String write(String service, List<Workload> workloads) {
    StringBuilder b = new StringBuilder();
    b.append("{\n  \"version\": 1,\n  \"service\": ").append(string(service)).append(",\n  \"workloads\": [");
    for (int i = 0; i < workloads.size(); i++) {
      Workload w = workloads.get(i);
      WorkloadOptions o = w.options();
      b.append(i == 0 ? "\n" : ",\n").append("    {\n");
      b.append("      \"name\": ").append(string(w.name()));
      o.schedule().ifPresent(s -> b.append(",\n      \"schedule\": ").append(string(s)));
      o.maxDuration().ifPresent(d -> b.append(",\n      \"maxDurationSeconds\": ").append(number(d.toMillis() / 1000.0)));
      o.expectEvery().ifPresent(d -> b.append(",\n      \"expectEverySeconds\": ").append(number(d.toMillis() / 1000.0)));
      o.minProcessed().ifPresent(n -> b.append(",\n      \"minProcessed\": ").append(n));
      o.failureRateThreshold().ifPresent(r -> b.append(",\n      \"failureRateThreshold\": ").append(number(r)));
      b.append(",\n      \"severity\": ").append(string(o.severity().manifestName())).append("\n    }");
    }
    b.append(workloads.isEmpty() ? "]\n}" : "\n  ]\n}");
    return b.toString();
  }

  // 1800.0 -> "1800", 0.2 -> "0.2", as JSON writers in the other SDKs print them.
  private static String number(double value) {
    return BigDecimal.valueOf(value).stripTrailingZeros().toPlainString();
  }

  private static String string(String value) {
    StringBuilder b = new StringBuilder(value.length() + 2).append('"');
    for (char c : value.toCharArray()) {
      switch (c) {
        case '"' -> b.append("\\\"");
        case '\\' -> b.append("\\\\");
        case '\n' -> b.append("\\n");
        case '\r' -> b.append("\\r");
        case '\t' -> b.append("\\t");
        default -> {
          if (c < 0x20) {
            b.append(String.format("\\u%04x", (int) c));
          } else {
            b.append(c);
          }
        }
      }
    }
    return b.append('"').toString();
  }
}
