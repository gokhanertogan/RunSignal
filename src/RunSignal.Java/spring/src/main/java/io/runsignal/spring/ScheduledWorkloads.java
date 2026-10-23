package io.runsignal.spring;

import io.runsignal.RunSignal;
import io.runsignal.Workload;
import io.runsignal.WorkloadNames;
import io.runsignal.WorkloadOptions;
import io.runsignal.WorkloadRegistry;
import java.lang.reflect.Method;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import org.springframework.core.annotation.AnnotatedElementUtils;
import org.springframework.core.env.Environment;
import org.springframework.scheduling.annotation.Scheduled;

/**
 * Maps {@code @Scheduled} methods to workloads, the same way whether a method is discovered at startup or runs
 * first: the name comes from {@link RunSignal @RunSignal} or the class and method
 * ({@code InvoiceJobs.generate} becomes {@code invoice-jobs.generate}), and the schedule from
 * {@code @Scheduled(cron = ...)}.
 */
public final class ScheduledWorkloads {
  private final WorkloadRegistry registry;
  private final Environment environment;
  private final RunSignalProperties properties;
  private final Map<Method, Workload> workloads = new ConcurrentHashMap<>();

  public ScheduledWorkloads(WorkloadRegistry registry, Environment environment, RunSignalProperties properties) {
    this.registry = registry;
    this.environment = environment;
    this.properties = properties;
  }

  /** The workload for a scheduled method, created on first use. {@code resolvedCron} may be null. */
  public Workload workloadFor(Class<?> targetClass, Method method, String resolvedCron) {
    return workloads.computeIfAbsent(method, m -> create(targetClass, m, resolvedCron));
  }

  private Workload create(Class<?> targetClass, Method method, String resolvedCron) {
    RunSignal annotation = AnnotatedElementUtils.findMergedAnnotation(method, RunSignal.class);
    if (annotation == null) {
      annotation = AnnotatedElementUtils.findMergedAnnotation(targetClass, RunSignal.class);
    }
    String name = annotation != null && !annotation.value().isEmpty()
        ? annotation.value()
        : WorkloadNames.toWorkloadName(targetClass.getSimpleName() + "." + method.getName());

    WorkloadOptions.Builder options = WorkloadOptions.builder();
    if (annotation != null) {
      if (!annotation.maxDuration().isEmpty()) options.maxDuration(annotation.maxDuration());
      if (!annotation.expectEvery().isEmpty()) options.expectEvery(annotation.expectEvery());
      if (annotation.minProcessed() > 0) options.minProcessed(annotation.minProcessed());
      if (annotation.failureRateThreshold() > 0) options.failureRateThreshold(annotation.failureRateThreshold());
      options.severity(annotation.severity());
    }
    String schedule = annotation != null && !annotation.schedule().isEmpty() ? annotation.schedule() : cronOf(method, resolvedCron);
    RunSignalProperties.Workload configured = properties.getWorkloads().get(name);
    if (configured != null) {
      return registry.getOrAdd(name, configured.toOptions(schedule));
    }
    if (schedule != null) options.schedule(schedule);
    return registry.getOrAdd(name, options.build());
  }

  private String cronOf(Method method, String resolvedCron) {
    String cron = resolvedCron;
    if (cron == null) {
      Scheduled scheduled = AnnotatedElementUtils.findMergedAnnotation(method, Scheduled.class);
      if (scheduled == null || scheduled.cron().isEmpty()) return null;
      cron = environment.resolvePlaceholders(scheduled.cron());
    }
    return standardCron(cron);
  }

  /**
   * Spring cron (seconds first, day of week 0-7) is already the format manifests use; macros are expanded and
   * the disabled marker "-" means no schedule.
   */
  static String standardCron(String cron) {
    if (cron == null || cron.isBlank() || cron.equals("-")) return null;
    return switch (cron.trim()) {
      case "@yearly", "@annually" -> "0 0 0 1 1 *";
      case "@monthly" -> "0 0 0 1 * *";
      case "@weekly" -> "0 0 0 * * 0";
      case "@daily", "@midnight" -> "0 0 0 * * *";
      case "@hourly" -> "0 0 * * * *";
      default -> cron.trim();
    };
  }
}
