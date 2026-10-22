package io.runsignal;

import io.opentelemetry.api.common.AttributeKey;
import io.opentelemetry.api.common.Attributes;
import io.opentelemetry.api.trace.Span;
import io.opentelemetry.api.trace.SpanKind;
import io.opentelemetry.api.trace.StatusCode;
import io.opentelemetry.context.Scope;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.UndeclaredThrowableException;
import java.util.concurrent.Callable;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutionException;

/**
 * A unit of background work, such as a scheduled job, a consumer or a sync loop. Get one from a
 * {@link WorkloadRegistry} once and keep it: every method is thread-safe.
 */
public final class Workload {
  static final AttributeKey<String> WORKLOAD = AttributeKey.stringKey(RunSignalTelemetry.ATTRIBUTE_WORKLOAD);
  static final AttributeKey<String> OUTCOME = AttributeKey.stringKey(RunSignalTelemetry.ATTRIBUTE_OUTCOME);
  static final AttributeKey<String> ITEM_RESULT = AttributeKey.stringKey(RunSignalTelemetry.ATTRIBUTE_ITEM_RESULT);
  static final AttributeKey<String> ERROR_TYPE = AttributeKey.stringKey(RunSignalTelemetry.ATTRIBUTE_ERROR_TYPE);

  /** Code that may throw checked exceptions, run as one run of a workload. */
  @FunctionalInterface
  public interface ThrowingRunnable {
    void run() throws Exception;
  }

  private final WorkloadRegistry registry;
  private final String name;
  private final WorkloadOptions options;

  final Attributes nameAttributes;
  final Attributes success;
  final Attributes failure;
  final Attributes cancelled;
  private final Attributes processed;
  private final Attributes failed;

  // System.nanoTime() values. Until the first success or heartbeat they hold the time the workload was created,
  // so a workload that never succeeds or never beats still reports a growing age.
  private volatile long lastSuccess;
  private volatile long lastHeartbeat;
  private volatile boolean hasStarted;
  private volatile boolean hasBeaten;

  Workload(WorkloadRegistry registry, String name, WorkloadOptions options) {
    this.registry = registry;
    this.name = name;
    this.options = options;
    this.nameAttributes = Attributes.of(WORKLOAD, name);
    this.success = Attributes.of(WORKLOAD, name, OUTCOME, "success");
    this.failure = Attributes.of(WORKLOAD, name, OUTCOME, "failure");
    this.cancelled = Attributes.of(WORKLOAD, name, OUTCOME, "cancelled");
    this.processed = Attributes.of(WORKLOAD, name, ITEM_RESULT, "processed");
    this.failed = Attributes.of(WORKLOAD, name, ITEM_RESULT, "failed");
    this.lastSuccess = this.lastHeartbeat = System.nanoTime();
  }

  /** The workload's stable name, such as {@code nightly-invoice}. */
  public String name() {
    return name;
  }

  /** What the workload is expected to do. */
  public WorkloadOptions options() {
    return options;
  }

  /**
   * Starts a run. End it with exactly one call to {@link WorkloadRun#succeed()}, {@link WorkloadRun#fail(Throwable)},
   * {@link WorkloadRun#failCode(String, String)} or {@link WorkloadRun#cancel()}. Prefer {@link #run(ThrowingRunnable)},
   * which ends the run for you and makes its span current.
   */
  public WorkloadRun start() {
    if (!hasStarted) {
      hasStarted = true;
    }
    Span span = registry.tracer() == null ? null
        : registry.tracer().spanBuilder(name).setSpanKind(SpanKind.INTERNAL).setAllAttributes(nameAttributes).startSpan();
    return new WorkloadRun(this, System.nanoTime(), span);
  }

  /**
   * Runs {@code work} as one run: it succeeds when {@code work} returns, is cancelled when it is interrupted or
   * cancelled, and fails when it throws. Exceptions are rethrown as they are (checked ones too).
   */
  public void run(ThrowingRunnable work) throws Exception {
    call(() -> {
      work.run();
      return null;
    });
  }

  /** Like {@link #run(ThrowingRunnable)}, returning {@code work}'s result. */
  public <T> T call(Callable<T> work) throws Exception {
    WorkloadRun run = start();
    Scope scope = run.span() == null ? Scope.noop() : run.span().makeCurrent();
    try {
      T result = work.call();
      run.succeed();
      return result;
    } catch (InterruptedException | CancellationException e) {
      run.cancel();
      throw e;
    } catch (Exception | Error e) {
      run.fail(e);
      throw e;
    } finally {
      scope.close();
    }
  }

  /** Records {@code count} successfully processed items. Recording 0 is useful: it reports that a run did nothing. */
  public void processed(long count) {
    if (count >= 0) {
      registry.instruments().items().add(count, processed);
    }
  }

  /** Records one successfully processed item. */
  public void processed() {
    processed(1);
  }

  /** Records {@code count} items that could not be processed. */
  public void itemFailed(long count) {
    if (count >= 0) {
      registry.instruments().items().add(count, failed);
    }
  }

  /** Records one item that could not be processed. */
  public void itemFailed() {
    itemFailed(1);
  }

  /** Records a heartbeat: the workload is alive and making progress. */
  public void beat() {
    lastHeartbeat = System.nanoTime();
    if (!hasBeaten) {
      hasBeaten = true;
    }
    registry.instruments().heartbeats().add(1, nameAttributes);
  }

  void complete(long start, Span span, String outcome, String errorType, Throwable error, String description) {
    long now = System.nanoTime();
    WorkloadRegistry.Instruments instruments = registry.instruments();
    Attributes outcomeAttributes = switch (outcome) {
      case "success" -> success;
      case "failure" -> failure;
      default -> cancelled;
    };
    instruments.runs().add(1, errorType == null ? outcomeAttributes : outcomeAttributes.toBuilder().put(ERROR_TYPE, errorType).build());
    instruments.duration().record((now - start) / 1e9, outcomeAttributes);
    if ("success".equals(outcome)) {
      lastSuccess = now;
    }

    if (span != null) {
      if (span.isRecording()) {
        span.setAttribute(OUTCOME, outcome);
        if ("failure".equals(outcome)) {
          span.setAttribute(ERROR_TYPE, errorType);
          if (error != null) {
            span.recordException(error);
          }
          span.setStatus(StatusCode.ERROR, description == null ? "" : description);
        } else if ("success".equals(outcome)) {
          span.setStatus(StatusCode.OK);
        }
      }
      span.end();
    }
  }

  // Reported for workloads that are scheduled or have run, so alerts can tell a missed run from a workload that
  // simply has no runs. Returns NaN when not reported.
  double secondsSinceSuccess(long now) {
    return hasStarted || options.schedule().isPresent() ? (now - lastSuccess) / 1e9 : Double.NaN;
  }

  // Reported for workloads that expect heartbeats or have sent one.
  double secondsSinceHeartbeat(long now) {
    return hasBeaten || options.expectEvery().isPresent() ? (now - lastHeartbeat) / 1e9 : Double.NaN;
  }

  /**
   * The {@code error.type} of a failure: the exception's {@link ErrorTyped#errorType()} if it implements it,
   * otherwise its class name, looking through wrappers such as {@link ExecutionException}.
   */
  static String errorTypeOf(Throwable error) {
    Throwable t = error;
    while ((t instanceof ExecutionException || t instanceof CompletionException || t instanceof InvocationTargetException
        || t instanceof UndeclaredThrowableException) && t.getCause() != null) {
      t = t.getCause();
    }
    return t instanceof ErrorTyped typed ? typed.errorType() : t.getClass().getName();
  }
}
