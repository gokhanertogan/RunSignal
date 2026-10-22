package io.runsignal;

import java.util.Locale;

/** How urgent a workload's alerts are; it routes notifications. */
public enum Severity {
  /** Worth knowing about; no action needed now. */
  INFO,
  /** Needs attention during working hours. The default. */
  WARNING,
  /** Needs attention now, for example by paging on-call. */
  CRITICAL;

  /** The severity as the manifest spells it: {@code info}, {@code warning} or {@code critical}. */
  public String manifestName() {
    return name().toLowerCase(Locale.ROOT);
  }
}
