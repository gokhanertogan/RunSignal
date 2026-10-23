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
import java.util.concurrent.ConcurrentHashMap;
import org.springframework.beans.factory.config.BeanPostProcessor;

/**
 * Records every Spring Batch job execution as a run of a workload named after the job: COMPLETED succeeds, FAILED
 * fails with the job's first exception (or its exit code), STOPPED and ABANDONED are cancelled. Items written by its
 * steps count as processed and skipped items as failed, so jobs need no code.
 *
 * <p>Spring Batch 6 moved its listener and execution types to new packages, so this works by reflection against
 * whichever version is present (5 or 6). It costs a few reflective calls per job execution.
 */
final class BatchJobListener implements BeanPostProcessor {
  private static final String[] LISTENER_TYPES = {
    "org.springframework.batch.core.listener.JobExecutionListener", // Spring Batch 6
    "org.springframework.batch.core.JobExecutionListener", // Spring Batch 5
  };

  private final WorkloadRegistry registry;
  private final RunSignalProperties properties;
  private final Map<Object, WorkloadRun> running = new ConcurrentHashMap<>();
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
    running.put(call(execution, "getId"), workloadFor(jobName).start());
  }

  private void afterJob(Object execution) throws ReflectiveOperationException {
    WorkloadRun run = running.remove(call(execution, "getId"));
    if (run == null) {
      return;
    }

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
