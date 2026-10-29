package io.runsignal;

import java.util.List;

/**
 * Names of the meter, tracer, instruments and attributes RunSignal emits. They are identical in every RunSignal
 * SDK (see spec/README.md in the repository), so one set of alert rules covers every language.
 */
public final class RunSignalTelemetry {
  /** Name of the OpenTelemetry meter and tracer. */
  public static final String SCOPE_NAME = "RunSignal";
  /** The SDK version, reported as the instrumentation scope version. */
  public static final String VERSION = "0.2.0";

  public static final String METRIC_RUNS = "runsignal.workload.runs";
  public static final String METRIC_DURATION = "runsignal.workload.duration";
  public static final String METRIC_ITEMS = "runsignal.workload.items";
  public static final String METRIC_HEARTBEATS = "runsignal.workload.heartbeats";
  public static final String METRIC_SINCE_SUCCESS = "runsignal.workload.since_success";
  public static final String METRIC_SINCE_HEARTBEAT = "runsignal.workload.since_heartbeat";

  public static final String ATTRIBUTE_WORKLOAD = "runsignal.workload";
  public static final String ATTRIBUTE_OUTCOME = "runsignal.outcome";
  public static final String ATTRIBUTE_ITEM_RESULT = "runsignal.item.result";
  public static final String ATTRIBUTE_ERROR_TYPE = "error.type";

  /**
   * Histogram bucket boundaries, in seconds, for the duration metric: background work runs from milliseconds to
   * hours, while the OpenTelemetry defaults stop at 10 seconds.
   */
  public static final List<Double> DURATION_BUCKETS = List.of(
      0.01, 0.05, 0.1, 0.5, 1.0, 5.0, 10.0, 30.0, 60.0, 120.0, 300.0, 600.0, 900.0, 1800.0, 3600.0, 7200.0, 14400.0, 28800.0);

  private RunSignalTelemetry() {}
}
