package io.runsignal.spring;

import io.micrometer.observation.ObservationRegistry;
import io.opentelemetry.api.OpenTelemetry;
import io.runsignal.WorkloadRegistry;
import java.nio.file.Path;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.Set;
import org.apache.commons.logging.Log;
import org.apache.commons.logging.LogFactory;
import org.springframework.beans.factory.ObjectProvider;
import org.springframework.boot.autoconfigure.AutoConfiguration;
import org.springframework.boot.autoconfigure.condition.ConditionalOnMissingBean;
import org.springframework.boot.context.event.ApplicationReadyEvent;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.context.ApplicationListener;
import org.springframework.context.annotation.Bean;
import org.springframework.core.env.Environment;
import org.springframework.scheduling.annotation.SchedulingConfigurer;
import org.springframework.scheduling.config.CronTask;
import org.springframework.scheduling.config.ScheduledTask;
import org.springframework.scheduling.config.ScheduledTaskHolder;
import org.springframework.scheduling.support.ScheduledMethodRunnable;
import org.springframework.util.StringUtils;

/**
 * Monitors {@code @Scheduled} methods with RunSignal: every run is recorded, cron jobs are registered as workloads
 * as soon as the application is ready (so a job that never fires is alerted on), and the manifest is written when
 * {@code runsignal.manifest-path} or RUNSIGNAL_MANIFEST_PATH is set.
 */
@AutoConfiguration
@EnableConfigurationProperties(RunSignalProperties.class)
public class RunSignalAutoConfiguration {
  private static final Log log = LogFactory.getLog(RunSignalAutoConfiguration.class);

  /** Records on the application's OpenTelemetry bean, or on GlobalOpenTelemetry (for example the Java agent). */
  @Bean
  @ConditionalOnMissingBean
  public WorkloadRegistry runSignalWorkloadRegistry(ObjectProvider<OpenTelemetry> openTelemetry, RunSignalProperties properties) {
    OpenTelemetry otel = openTelemetry.getIfAvailable();
    if (otel != null) {
      return new WorkloadRegistry(otel, properties.isTracing());
    }
    return WorkloadRegistry.global();
  }

  @Bean
  public ScheduledWorkloads runSignalScheduledWorkloads(WorkloadRegistry registry, Environment environment, RunSignalProperties properties) {
    return new ScheduledWorkloads(registry, environment, properties);
  }

  /** Monitors Spring Batch jobs when Spring Batch 5 or 6 is on the classpath. */
  @Bean
  public static org.springframework.beans.factory.config.BeanPostProcessor runSignalBatchJobListener(
      ObjectProvider<WorkloadRegistry> registry, ObjectProvider<RunSignalProperties> properties, org.springframework.context.ApplicationContext context) {
    ClassLoader classLoader = context.getClassLoader();
    if (classLoader == null || !BatchJobListener.batchPresent(classLoader)) {
      return new org.springframework.beans.factory.config.BeanPostProcessor() { };
    }
    return new LazyBatchJobListener(registry, properties, classLoader);
  }

  // Bean post-processors are created early; resolve the registry only when the first job bean appears.
  private static final class LazyBatchJobListener implements org.springframework.beans.factory.config.BeanPostProcessor {
    private final ObjectProvider<WorkloadRegistry> registry;
    private final ObjectProvider<RunSignalProperties> properties;
    private final ClassLoader classLoader;
    private volatile BatchJobListener delegate;

    LazyBatchJobListener(ObjectProvider<WorkloadRegistry> registry, ObjectProvider<RunSignalProperties> properties, ClassLoader classLoader) {
      this.registry = registry;
      this.properties = properties;
      this.classLoader = classLoader;
    }

    @Override
    public Object postProcessAfterInitialization(Object bean, String beanName) {
      if (!isJob(bean)) {
        return bean;
      }
      BatchJobListener d = delegate;
      if (d == null) {
        synchronized (this) {
          if (delegate == null) {
            delegate = new BatchJobListener(registry.getObject(), properties.getObject(), classLoader);
          }
          d = delegate;
        }
      }
      return d.postProcessAfterInitialization(bean, beanName);
    }

    private static boolean isJob(Object bean) {
      for (Class<?> c = bean.getClass(); c != null; c = c.getSuperclass()) {
        if (c.getName().equals("org.springframework.batch.core.job.AbstractJob")) {
          return true;
        }
      }
      return false;
    }
  }

  @Bean
  public RunSignalObservationHandler runSignalObservationHandler(ScheduledWorkloads workloads) {
    return new RunSignalObservationHandler(workloads);
  }

  /**
   * Makes Spring observe {@code @Scheduled} runs, on the application's ObservationRegistry if it has one, and adds
   * the RunSignal handler to it.
   */
  @Bean
  public SchedulingConfigurer runSignalSchedulingConfigurer(ObjectProvider<ObservationRegistry> observationRegistries,
      RunSignalObservationHandler handler) {
    Set<ObservationRegistry> attached = Collections.newSetFromMap(new IdentityHashMap<>());
    return registrar -> {
      ObservationRegistry registry = registrar.getObservationRegistry();
      if (registry == null || registry.isNoop()) {
        registry = observationRegistries.getIfAvailable(ObservationRegistry::create);
      }
      if (attached.add(registry)) {
        registry.observationConfig().observationHandler(handler);
      }
      registrar.setObservationRegistry(registry);
    };
  }

  /** Registers scheduled cron jobs before they first fire, then writes the manifest if asked to. */
  @Bean
  public ApplicationListener<ApplicationReadyEvent> runSignalDiscovery(ObjectProvider<ScheduledTaskHolder> holders,
      ScheduledWorkloads workloads, WorkloadRegistry registry, RunSignalProperties properties, Environment environment) {
    return event -> {
      holders.orderedStream().forEach(holder -> {
        for (ScheduledTask scheduled : holder.getScheduledTasks()) {
          if (scheduled.getTask() instanceof CronTask cron && unwrap(cron.getRunnable()) instanceof ScheduledMethodRunnable method) {
            workloads.workloadFor(org.springframework.aop.support.AopUtils.getTargetClass(method.getTarget()), method.getMethod(), cron.getExpression());
          }
        }
      });

      String path = StringUtils.hasText(properties.getManifestPath()) ? properties.getManifestPath() : System.getenv("RUNSIGNAL_MANIFEST_PATH");
      if (!StringUtils.hasText(path)) {
        return;
      }
      String service = firstText(properties.getServiceName(), environment.getProperty("spring.application.name"), System.getenv("OTEL_SERVICE_NAME"));
      if (service == null) {
        log.warn("RunSignal did not write the manifest: set runsignal.service-name or spring.application.name.");
        return;
      }
      try {
        registry.writeManifest(Path.of(path.trim()), service);
        log.info("RunSignal wrote a manifest of " + registry.workloads().size() + " workloads to " + path.trim());
      } catch (RuntimeException e) {
        log.warn("RunSignal could not write the manifest to " + path.trim(), e);
      }
    };
  }

  // Spring wraps scheduled runnables to track their outcome; the method runnable is inside.
  private static Runnable unwrap(Runnable runnable) {
    Runnable current = runnable;
    for (int i = 0; i < 5 && !(current instanceof ScheduledMethodRunnable); i++) {
      Runnable inner = innerRunnable(current);
      if (inner == null) break;
      current = inner;
    }
    return current;
  }

  private static Runnable innerRunnable(Runnable wrapper) {
    for (var field : wrapper.getClass().getDeclaredFields()) {
      if (Runnable.class.isAssignableFrom(field.getType())) {
        try {
          field.setAccessible(true);
          return (Runnable) field.get(wrapper);
        } catch (ReflectiveOperationException | RuntimeException e) {
          return null;
        }
      }
    }
    return null;
  }

  private static String firstText(String... values) {
    for (String v : values) {
      if (StringUtils.hasText(v)) return v;
    }
    return null;
  }
}
