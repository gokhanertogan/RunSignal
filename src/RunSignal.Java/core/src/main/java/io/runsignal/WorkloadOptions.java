package io.runsignal;

import java.time.Duration;
import java.util.Locale;
import java.util.Objects;
import java.util.Optional;
import java.util.OptionalDouble;
import java.util.OptionalLong;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * What a workload is expected to do. These define the alerts RunSignal provisions and do not change how signals
 * are recorded. Create with {@link #builder()}.
 */
public final class WorkloadOptions {
  private static final Pattern DURATION = Pattern.compile("(\\d+(?:\\.\\d+)?)([smhd])");
  /** No expectations. */
  public static final WorkloadOptions NONE = builder().build();

  private final String schedule;
  private final Duration maxDuration;
  private final Duration expectEvery;
  private final long minProcessed;
  private final double failureRateThreshold;
  private final Severity severity;

  private WorkloadOptions(Builder b) {
    this.schedule = b.schedule;
    this.maxDuration = b.maxDuration;
    this.expectEvery = b.expectEvery;
    this.minProcessed = b.minProcessed;
    this.failureRateThreshold = b.failureRateThreshold;
    this.severity = b.severity;
  }

  public static Builder builder() {
    return new Builder();
  }

  /** Cron expression for when the workload should run. Produces the missed alert. */
  public Optional<String> schedule() {
    return Optional.ofNullable(schedule);
  }

  /** Longest acceptable run. Produces the overrun alert. */
  public Optional<Duration> maxDuration() {
    return Optional.ofNullable(maxDuration);
  }

  /** Longest acceptable gap between heartbeats. Produces the stalled alert. */
  public Optional<Duration> expectEvery() {
    return Optional.ofNullable(expectEvery);
  }

  /** Fewest items a run should process. Produces the low-output alert. */
  public OptionalLong minProcessed() {
    return minProcessed > 0 ? OptionalLong.of(minProcessed) : OptionalLong.empty();
  }

  /** Highest acceptable share (0 to 1) of failed runs or items. Produces the failure-rate alert. */
  public OptionalDouble failureRateThreshold() {
    return failureRateThreshold > 0 ? OptionalDouble.of(failureRateThreshold) : OptionalDouble.empty();
  }

  /** How urgent the workload's alerts are. */
  public Severity severity() {
    return severity;
  }

  /** Builds {@link WorkloadOptions}. */
  public static final class Builder {
    private String schedule;
    private Duration maxDuration;
    private Duration expectEvery;
    private long minProcessed;
    private double failureRateThreshold;
    private Severity severity = Severity.WARNING;

    private Builder() {}

    public Builder schedule(String cron) {
      this.schedule = cron;
      return this;
    }

    public Builder maxDuration(Duration maxDuration) {
      this.maxDuration = positive(maxDuration, "maxDuration");
      return this;
    }

    /** Like {@link #maxDuration(Duration)}, from a string such as {@code "90s"}, {@code "30m"}, {@code "2h"} or {@code "1d"}. */
    public Builder maxDuration(String maxDuration) {
      return maxDuration(parseDuration(maxDuration, "maxDuration"));
    }

    public Builder expectEvery(Duration expectEvery) {
      this.expectEvery = positive(expectEvery, "expectEvery");
      return this;
    }

    /** Like {@link #expectEvery(Duration)}, from a string such as {@code "5m"}. */
    public Builder expectEvery(String expectEvery) {
      return expectEvery(parseDuration(expectEvery, "expectEvery"));
    }

    public Builder minProcessed(long minProcessed) {
      if (minProcessed < 0) {
        throw new IllegalArgumentException("minProcessed must not be negative");
      }
      this.minProcessed = minProcessed;
      return this;
    }

    public Builder failureRateThreshold(double rate) {
      if (rate < 0 || rate > 1 || Double.isNaN(rate)) {
        throw new IllegalArgumentException("failureRateThreshold must be between 0 and 1, got " + rate);
      }
      this.failureRateThreshold = rate;
      return this;
    }

    public Builder severity(Severity severity) {
      this.severity = Objects.requireNonNull(severity, "severity");
      return this;
    }

    public WorkloadOptions build() {
      return new WorkloadOptions(this);
    }

    private static Duration positive(Duration d, String option) {
      if (d == null || d.isNegative() || d.isZero()) {
        throw new IllegalArgumentException(option + " must be positive");
      }
      return d;
    }
  }

  /** Parses {@code "90s"}, {@code "30m"}, {@code "1.5h"} or {@code "1d"}. */
  static Duration parseDuration(String value, String option) {
    Matcher m = DURATION.matcher(value == null ? "" : value.trim().toLowerCase(Locale.ROOT));
    if (!m.matches()) {
      throw new IllegalArgumentException(option + " '" + value + "' is not a duration. Use a number with s, m, h or d, such as \"30m\".");
    }
    double amount = Double.parseDouble(m.group(1));
    long seconds = switch (m.group(2)) {
      case "s" -> 1;
      case "m" -> 60;
      case "h" -> 3600;
      default -> 86400;
    };
    return Duration.ofMillis(Math.round(amount * seconds * 1000));
  }
}
