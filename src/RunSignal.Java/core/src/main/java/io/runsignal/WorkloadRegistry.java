package io.runsignal;

import io.opentelemetry.api.GlobalOpenTelemetry;
import io.opentelemetry.api.OpenTelemetry;
import io.opentelemetry.api.metrics.DoubleHistogram;
import io.opentelemetry.api.metrics.LongCounter;
import io.opentelemetry.api.metrics.Meter;
import io.opentelemetry.api.metrics.ObservableDoubleMeasurement;
import io.opentelemetry.api.trace.Tracer;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Supplier;

/** Creates and holds the workloads of an application and the instruments they record on. */
public final class WorkloadRegistry {
  record Instruments(LongCounter runs, DoubleHistogram duration, LongCounter items, LongCounter heartbeats) {}

  private static final WorkloadRegistry GLOBAL = new WorkloadRegistry(GlobalOpenTelemetry::get, true, true);

  private final Map<String, Workload> workloads = new ConcurrentHashMap<>();
  private final Supplier<OpenTelemetry> openTelemetry;
  private final boolean tracing;
  private final boolean global;
  private volatile Instruments instruments;
  private volatile Tracer tracer;

  /** A registry that records on {@code openTelemetry}, with a span per run. */
  public WorkloadRegistry(OpenTelemetry openTelemetry) {
    this(openTelemetry, true);
  }

  /**
   * A registry that records on {@code openTelemetry}. With {@code tracing} false it records no span per run, for
   * workloads that run thousands of times per second.
   */
  public WorkloadRegistry(OpenTelemetry openTelemetry, boolean tracing) {
    this(() -> openTelemetry, tracing, false);
  }

  private WorkloadRegistry(Supplier<OpenTelemetry> openTelemetry, boolean tracing, boolean global) {
    this.openTelemetry = openTelemetry;
    this.tracing = tracing;
    this.global = global;
  }

  /**
   * A process-wide registry on {@link GlobalOpenTelemetry}. It resolves the global instance on first use, not
   * when it is created: calling {@code GlobalOpenTelemetry.get()} before the SDK or the Java agent registers would
   * fix it to a no-op for good.
   */
  public static WorkloadRegistry global() {
    return GLOBAL;
  }

  /**
   * Forgets the workloads and instruments of this registry, for tests that replace GlobalOpenTelemetry with
   * {@code GlobalOpenTelemetry.resetForTest()}: the global registry would otherwise keep recording on the instance
   * it first resolved.
   */
  synchronized void resetForTest() {
    workloads.clear();
    instruments = null;
    tracer = null;
  }

  /**
   * Returns the workload called {@code name}, creating it with {@code options} on first use. Call it once and keep
   * the result. Passing options for a workload that already exists throws, so expectations are declared in exactly
   * one place.
   */
  public Workload workload(String name, WorkloadOptions options) {
    Workload existing = workloads.get(name);
    if (existing != null) {
      throw new IllegalStateException("Workload '" + name + "' is already registered; set its options only where it is first created.");
    }
    WorkloadNames.validate(name);
    Workload created = new Workload(this, name, options);
    Workload raced = workloads.putIfAbsent(name, created);
    if (raced != null) {
      throw new IllegalStateException("Workload '" + name + "' is already registered; set its options only where it is first created.");
    }
    registerGauges();
    return created;
  }

  /** Returns the workload called {@code name}, creating it without expectations on first use. */
  public Workload workload(String name) {
    return getOrAdd(name, WorkloadOptions.NONE);
  }

  /**
   * Returns the workload called {@code name}, creating it with {@code options} if it does not exist yet. Unlike
   * {@link #workload(String, WorkloadOptions)}, an existing workload is returned as is: options declared elsewhere
   * take precedence. Scheduler integrations use it to name workloads after jobs.
   */
  public Workload getOrAdd(String name, WorkloadOptions options) {
    Workload existing = workloads.get(name);
    if (existing != null) {
      return existing;
    }
    WorkloadNames.validate(name);
    Workload workload = workloads.computeIfAbsent(name, n -> new Workload(this, n, options));
    registerGauges();
    return workload;
  }

  /**
   * Creates the instruments, and with them the gauges, as soon as a workload exists, so a workload that never runs
   * still reports {@code since_success} (and {@code active_run_age}) and its missed run is alerted on. The global
   * registry waits until GlobalOpenTelemetry is set: reading it earlier would fix it to a no-op for good. Until
   * then its instruments are created by the first run, item or heartbeat, as before.
   */
  private void registerGauges() {
    if (instruments == null && (!global || globalIsSet())) {
      instruments();
    }
  }

  private static boolean globalIsSet() {
    try {
      return GlobalOpenTelemetry.isSet();
    } catch (NoSuchMethodError e) {
      return false; // older opentelemetry-api versions have no isSet(): wait for the first record, as before
    }
  }

  /** Every workload created so far, sorted by name. */
  public List<Workload> workloads() {
    List<Workload> list = new ArrayList<>(workloads.values());
    list.sort(Comparator.comparing(Workload::name));
    return list;
  }

  Tracer tracer() {
    if (!tracing) {
      return null;
    }
    Tracer t = tracer;
    if (t == null) {
      t = openTelemetry.get().getTracer(RunSignalTelemetry.SCOPE_NAME, RunSignalTelemetry.VERSION);
      tracer = t;
    }
    return t;
  }

  Instruments instruments() {
    Instruments i = instruments;
    if (i == null) {
      synchronized (this) {
        i = instruments;
        if (i == null) {
          i = createInstruments(openTelemetry.get().getMeter(RunSignalTelemetry.SCOPE_NAME));
          instruments = i;
        }
      }
    }
    return i;
  }

  private Instruments createInstruments(Meter meter) {
    var sinceSuccess = meter.gaugeBuilder(RunSignalTelemetry.METRIC_SINCE_SUCCESS).setUnit("s")
        .setDescription("Seconds since the last successful run of a workload, or since it was registered.").buildObserver();
    var sinceHeartbeat = meter.gaugeBuilder(RunSignalTelemetry.METRIC_SINCE_HEARTBEAT).setUnit("s")
        .setDescription("Seconds since the last heartbeat of a workload, or since it was registered.").buildObserver();
    var activeRunAge = meter.gaugeBuilder(RunSignalTelemetry.METRIC_ACTIVE_RUN_AGE).setUnit("s")
        .setDescription("Seconds the oldest unfinished run of a workload has been running, or 0.").buildObserver();
    meter.batchCallback(() -> observe(sinceSuccess, sinceHeartbeat, activeRunAge), sinceSuccess, sinceHeartbeat, activeRunAge);

    return new Instruments(
        meter.counterBuilder(RunSignalTelemetry.METRIC_RUNS).setUnit("{run}").setDescription("Completed runs of a workload.").build(),
        meter.histogramBuilder(RunSignalTelemetry.METRIC_DURATION).setUnit("s").setDescription("Duration of workload runs.")
            .setExplicitBucketBoundariesAdvice(RunSignalTelemetry.DURATION_BUCKETS).build(),
        meter.counterBuilder(RunSignalTelemetry.METRIC_ITEMS).setUnit("{item}").setDescription("Items handled by workload runs.").build(),
        meter.counterBuilder(RunSignalTelemetry.METRIC_HEARTBEATS).setUnit("{heartbeat}").setDescription("Heartbeats of long-running workloads.").build());
  }

  private void observe(ObservableDoubleMeasurement sinceSuccess, ObservableDoubleMeasurement sinceHeartbeat,
      ObservableDoubleMeasurement activeRunAge) {
    long now = System.nanoTime();
    for (Workload w : workloads.values()) {
      double success = w.secondsSinceSuccess(now);
      if (!Double.isNaN(success)) {
        sinceSuccess.record(success, w.nameAttributes);
      }
      double heartbeat = w.secondsSinceHeartbeat(now);
      if (!Double.isNaN(heartbeat)) {
        sinceHeartbeat.record(heartbeat, w.nameAttributes);
      }
      double age = w.activeRunAge(now);
      if (!Double.isNaN(age)) {
        activeRunAge.record(age, w.nameAttributes);
      }
    }
  }

  /**
   * The workload manifest as JSON, in the format of spec/manifest.schema.json. {@code service} must match the
   * OpenTelemetry {@code service.name} the signals are exported with.
   */
  public String manifestJson(String service) {
    return ManifestJson.write(service, workloads());
  }

  /** Writes the manifest to {@code path} atomically, so a reader never sees half a manifest. */
  public void writeManifest(Path path, String service) {
    try {
      Path parent = path.toAbsolutePath().getParent();
      if (parent != null) {
        Files.createDirectories(parent);
      }
      Path tmp = path.resolveSibling(path.getFileName() + ".tmp");
      Files.writeString(tmp, manifestJson(service), StandardCharsets.UTF_8);
      Files.move(tmp, path, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
    } catch (IOException e) {
      throw new UncheckedIOException("Could not write the RunSignal manifest to " + path, e);
    }
  }

  /**
   * Writes the manifest if the RUNSIGNAL_MANIFEST_PATH environment variable is set, so CI can ask for one without
   * code changes. Call it once every workload is registered. The service name is {@code service}, or else
   * OTEL_SERVICE_NAME. Returns the path written, if any.
   */
  public Optional<Path> writeManifestFromEnv(String service) {
    String path = System.getenv("RUNSIGNAL_MANIFEST_PATH");
    if (path == null || path.isBlank()) {
      return Optional.empty();
    }
    String name = service != null && !service.isBlank() ? service : System.getenv("OTEL_SERVICE_NAME");
    if (name == null || name.isBlank()) {
      throw new IllegalArgumentException("Pass a service name or set OTEL_SERVICE_NAME to write the manifest.");
    }
    Path target = Path.of(path.trim());
    writeManifest(target, name);
    return Optional.of(target);
  }
}
