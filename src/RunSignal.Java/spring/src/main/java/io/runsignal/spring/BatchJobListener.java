package io.runsignal.spring;

import io.runsignal.Workload;
import io.runsignal.WorkloadNames;
import io.runsignal.WorkloadOptions;
import io.runsignal.WorkloadRegistry;
import io.runsignal.WorkloadRun;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.Collection;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.time.Duration;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import org.springframework.beans.factory.config.BeanPostProcessor;

/**
 * Records every Spring Batch job execution as a run of a workload named after the job: COMPLETED succeeds, FAILED
 * fails with the job's first exception (or its exit code), STOPPED and ABANDONED are cancelled. Items written by its
 * steps count as processed and skipped items as failed, so jobs need no code.
 *
 * <p>The run is in progress (and counts towards {@code active_run_age}) from {@code beforeJob} to {@code afterJob}.
 * Spring Batch calls {@code afterJob} even when the job fails or is interrupted. Spring Batch 6 calls it after saving
 * the finished execution to the job repository, and skips it when that save throws (Batch 5 calls it before the
 * save, so it is not affected). Such a run would look hung forever, so a background sweep ends
 * it as a failure ({@code job_repository_update_failed}) once the execution has had an end time for
 * {@link #sweepGrace} without {@code afterJob} being called: the job finished, but the repository still says it is
 * running, which blocks restarting it.
 *
 * <p>Spring Batch 6 moved its listener and execution types to new packages, so this works by reflection against
 * whichever version is present (5 or 6). It costs a few reflective calls per job execution.
 */
final class BatchJobListener implements BeanPostProcessor, AutoCloseable {
  /** The error type of a run whose afterJob never came after its execution ended. */
  static final String UPDATE_FAILED = "job_repository_update_failed";

  // How long an ended execution may wait for afterJob, and how often to look. Spring Batch sets the end time just
  // before the final repository update and calls afterJob right after it, so the wait is normally milliseconds.
  // Settable for tests.
  static volatile Duration sweepGrace = Duration.ofSeconds(30);
  static volatile Duration sweepInterval = Duration.ofSeconds(30);

  private static final String[] LISTENER_TYPES = {
    "org.springframework.batch.core.listener.JobExecutionListener", // Spring Batch 6
    "org.springframework.batch.core.JobExecutionListener", // Spring Batch 5
  };

  private final WorkloadRegistry registry;
  private final RunSignalProperties properties;
  private final Map<Object, Pending> running = new ConcurrentHashMap<>();
  private volatile ScheduledExecutorService sweeper;

  // A job execution between beforeJob and afterJob.
  private static final class Pending {
    final WorkloadRun run;
    final Object execution;
    long endSeenAt; // System.nanoTime() when the sweep first saw an end time, or 0; only the sweep thread uses it

    Pending(WorkloadRun run, Object execution) {
      this.run = run;
      this.execution = execution;
    }
  }
  private final Class<?> listenerType;
  private final Object listener;

  BatchJobListener(WorkloadRegistry registry, RunSignalProperties properties, ClassLoader classLoader) {
    this.registry = registry;
    this.properties = properties;
    this.listenerType = firstLoadable(classLoader);
    this.listener = listenerType == null ? null
        : Proxy.newProxyInstance(classLoader, new Class<?>[] {listenerType}, handler());
  }

  static boolean batchPresent(ClassLoader classLoader) {
    return firstLoadable(classLoader) != null;
  }

  /** Adds the listener to every job and registers the job as a workload, so it is known before it first runs. */
  @Override
  public Object postProcessAfterInitialization(Object bean, String beanName) {
    if (listener == null) {
      return bean;
    }
    for (Method m : bean.getClass().getMethods()) {
      if (m.getName().equals("registerJobExecutionListener") && m.getParameterCount() == 1
          && m.getParameterTypes()[0].isAssignableFrom(listenerType)) {
        try {
          m.invoke(bean, listener);
          Object name = bean.getClass().getMethod("getName").invoke(bean);
          if (name instanceof String jobName) {
            workloadFor(jobName);
          }
        } catch (ReflectiveOperationException e) {
          throw new IllegalStateException("RunSignal could not add its listener to Spring Batch job " + beanName, e);
        }
        break;
      }
    }
    return bean;
  }

  private Workload workloadFor(String jobName) {
    String name = WorkloadNames.toWorkloadName(jobName);
    RunSignalProperties.Workload configured = properties.getWorkloads().get(name);
    return registry.getOrAdd(name, configured != null ? configured.toOptions(null) : WorkloadOptions.NONE);
  }

  private InvocationHandler handler() {
    return (proxy, method, args) -> switch (method.getName()) {
      case "beforeJob" -> {
        beforeJob(args[0]);
        yield null;
      }
      case "afterJob" -> {
        afterJob(args[0]);
        yield null;
      }
      case "equals" -> proxy == args[0];
      case "hashCode" -> System.identityHashCode(proxy);
      case "toString" -> "RunSignal Spring Batch listener";
      default -> null;
    };
  }

  private void beforeJob(Object execution) throws ReflectiveOperationException {
    Object instance = call(execution, "getJobInstance");
    String jobName = (String) call(instance, "getJobName");
    running.put(call(execution, "getId"), new Pending(workloadFor(jobName).start(), execution));
    startSweeper();
  }

  private void afterJob(Object execution) throws ReflectiveOperationException {
    Pending pending = running.remove(call(execution, "getId"));
    if (pending == null) {
      return;
    }
    WorkloadRun run = pending.run;
    recordItems(run, execution);

    String status = String.valueOf(call(execution, "getStatus"));
    switch (status) {
      case "COMPLETED" -> run.succeed();
      case "STOPPED", "STOPPING", "ABANDONED" -> run.cancel();
      default -> {
        List<?> failures = (List<?>) call(execution, "getAllFailureExceptions");
        if (!failures.isEmpty() && failures.get(0) instanceof Throwable error) {
          run.fail(error);
        } else {
          Object exitCode = call(call(execution, "getExitStatus"), "getExitCode");
          run.failCode(String.valueOf(exitCode).toLowerCase(Locale.ROOT), "Spring Batch status " + status);
        }
      }
    }
  }

  private static void recordItems(WorkloadRun run, Object execution) throws ReflectiveOperationException {
    long written = 0;
    long skipped = 0;
    for (Object step : (Collection<?>) call(execution, "getStepExecutions")) {
      written += ((Number) call(step, "getWriteCount")).longValue();
      skipped += ((Number) call(step, "getSkipCount")).longValue();
    }
    run.workload().processed(written);
    if (skipped > 0) {
      run.workload().itemFailed(skipped);
    }
  }

  private void startSweeper() {
    if (sweeper != null) {
      return;
    }
    synchronized (this) {
      if (sweeper == null) {
        ScheduledExecutorService s = Executors.newSingleThreadScheduledExecutor(r -> {
          Thread t = new Thread(r, "runsignal-batch-sweep");
          t.setDaemon(true);
          return t;
        });
        long interval = Math.max(1, sweepInterval.toMillis());
        s.scheduleWithFixedDelay(this::sweep, interval, interval, TimeUnit.MILLISECONDS);
        sweeper = s;
      }
    }
  }

  /** Ends runs whose execution ended but whose afterJob never came (see the class comment). */
  void sweep() {
    long now = System.nanoTime();
    long grace = sweepGrace.toNanos();
    for (Map.Entry<Object, Pending> entry : running.entrySet()) {
      Pending pending = entry.getValue();
      try {
        if (call(pending.execution, "getEndTime") == null) {
          continue;
        }
        if (pending.endSeenAt == 0) {
          pending.endSeenAt = now == 0 ? 1 : now;
        } else if (now - pending.endSeenAt >= grace && running.remove(entry.getKey(), pending)) {
          try {
            recordItems(pending.run, pending.execution);
          } finally {
            pending.run.failCode(UPDATE_FAILED, "Spring Batch did not call afterJob after the job ended; saving the "
                + "execution to the job repository probably failed");
          }
        }
      } catch (ReflectiveOperationException | RuntimeException e) {
        // Never let one execution stop the sweep: a scheduled task that throws is not run again.
      }
    }
  }

  @Override
  public void close() {
    ScheduledExecutorService s = sweeper;
    if (s != null) {
      s.shutdownNow();
    }
  }

  private static Object call(Object target, String method) throws ReflectiveOperationException {
    return target.getClass().getMethod(method).invoke(target);
  }

  private static Class<?> firstLoadable(ClassLoader classLoader) {
    for (String name : LISTENER_TYPES) {
      try {
        return Class.forName(name, false, classLoader);
      } catch (ClassNotFoundException e) {
        // try the next version's name
      }
    }
    return null;
  }
}
