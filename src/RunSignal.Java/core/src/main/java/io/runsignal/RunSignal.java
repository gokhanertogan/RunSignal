package io.runsignal;

import java.lang.annotation.Documented;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Declares a job method or class as a workload and sets its expectations. Scheduler integrations (such as the
 * Spring {@code @Scheduled} support in runsignal-spring) read it; without it they name the workload after the
 * class and method.
 *
 * <pre>{@code
 * @Scheduled(cron = "0 0 2 * * *")
 * @RunSignal(value = "nightly-invoice", maxDuration = "30m", minProcessed = 100, severity = Severity.CRITICAL)
 * void generateInvoices() { ... }
 * }</pre>
 */
@Documented
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.TYPE})
public @interface RunSignal {
  /** The workload name; empty to derive one from the class and method. */
  String value() default "";

  /** Cron expression; empty to use the scheduler's own (such as {@code @Scheduled(cron = ...)}). */
  String schedule() default "";

  /** Longest acceptable run, such as {@code "30m"}; empty for none. */
  String maxDuration() default "";

  /** Longest acceptable gap between heartbeats, such as {@code "5m"}; empty for none. */
  String expectEvery() default "";

  /** Fewest items a run should process; 0 for none. */
  long minProcessed() default 0;

  /** Highest acceptable share (0 to 1) of failed runs or items; 0 for none. */
  double failureRateThreshold() default 0;

  Severity severity() default Severity.WARNING;
}
