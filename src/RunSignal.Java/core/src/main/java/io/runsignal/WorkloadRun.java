package io.runsignal;

import io.opentelemetry.api.trace.Span;
import java.time.Duration;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * One run of a {@link Workload}, started with {@link Workload#start()}. End it with exactly one call to
 * {@link #succeed()}, {@link #fail(Throwable)}, {@link #failCode(String, String)} or {@link #cancel()}; later calls
 * are ignored. A run that is never ended records no outcome and stays in progress: the workload's
 * {@code active_run_age} gauge keeps growing, which opens the {@code hung} alert once it passes {@code maxDuration}.
 */
public final class WorkloadRun {
  private final Workload workload;
  private final long start;
  private final int slot;
  private final Span span;
  private final AtomicBoolean ended = new AtomicBoolean();

  WorkloadRun(Workload workload, long start, int slot, Span span) {
    this.workload = workload;
    this.start = start;
    this.slot = slot;
    this.span = span;
  }

  public Workload workload() {
    return workload;
  }

  /** The run's span, or {@code null} when tracing is off. */
  public Span span() {
    return span;
  }

  public Duration elapsed() {
    return Duration.ofNanos(System.nanoTime() - start);
  }

  /** Ends the run as successful. */
  public void succeed() {
    if (ended.compareAndSet(false, true)) {
      workload.complete(start, slot, span, "success", null, null, null);
    }
  }

  /**
   * Ends the run as failed because of {@code error}. Its {@code error.type} is the exception's
   * {@link ErrorTyped#errorType()} if it implements it, otherwise its class name.
   */
  public void fail(Throwable error) {
    if (ended.compareAndSet(false, true)) {
      workload.complete(start, slot, span, "failure", Workload.errorTypeOf(error), error, error.getMessage());
    }
  }

  /**
   * Ends the run as failed without an exception, for example when an expected input file is missing. {@code code}
   * must be a short, stable value such as {@code statement_missing}; never put messages or IDs in it.
   */
  public void failCode(String code, String description) {
    if (ended.compareAndSet(false, true)) {
      workload.complete(start, slot, span, "failure", code == null || code.isBlank() ? "unknown" : code, null, description);
    }
  }

  /** Ends the run as cancelled, for example on shutdown. Cancelled runs do not count as failures. */
  public void cancel() {
    if (ended.compareAndSet(false, true)) {
      workload.complete(start, slot, span, "cancelled", null, null, null);
    }
  }
}
