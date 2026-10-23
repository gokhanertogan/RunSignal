package io.runsignal.spring;

import org.springframework.boot.context.properties.ConfigurationProperties;

/** Settings under {@code runsignal.*}. */
@ConfigurationProperties("runsignal")
public class RunSignalProperties {
  /** Where to write the workload manifest once the application is ready; defaults to RUNSIGNAL_MANIFEST_PATH. */
  private String manifestPath;

  /** The service name in the manifest; defaults to spring.application.name, then OTEL_SERVICE_NAME. */
  private String serviceName;

  /** Records a span per run. Turn off for workloads that run thousands of times per second. */
  private boolean tracing = true;

  /**
   * Expectations per workload name, such as {@code runsignal.workloads.nightly-invoice.max-duration=30m}. They
   * apply to workloads the starter creates (Spring Batch jobs and {@code @Scheduled} methods), and take precedence
   * over {@code @RunSignal} so they can differ per environment.
   */
  private java.util.Map<String, Workload> workloads = new java.util.LinkedHashMap<>();

  /** Expectations for one workload. */
  public static class Workload {
    private String schedule;
    private java.time.Duration maxDuration;
    private java.time.Duration expectEvery;
    private long minProcessed;
    private double failureRateThreshold;
    private io.runsignal.Severity severity;

    public String getSchedule() { return schedule; }
    public void setSchedule(String schedule) { this.schedule = schedule; }
    public java.time.Duration getMaxDuration() { return maxDuration; }
    public void setMaxDuration(java.time.Duration maxDuration) { this.maxDuration = maxDuration; }
    public java.time.Duration getExpectEvery() { return expectEvery; }
    public void setExpectEvery(java.time.Duration expectEvery) { this.expectEvery = expectEvery; }
    public long getMinProcessed() { return minProcessed; }
    public void setMinProcessed(long minProcessed) { this.minProcessed = minProcessed; }
    public double getFailureRateThreshold() { return failureRateThreshold; }
    public void setFailureRateThreshold(double failureRateThreshold) { this.failureRateThreshold = failureRateThreshold; }
    public io.runsignal.Severity getSeverity() { return severity; }
    public void setSeverity(io.runsignal.Severity severity) { this.severity = severity; }

    /** These expectations as options; {@code schedule} is used when none is configured. */
    io.runsignal.WorkloadOptions toOptions(String fallbackSchedule) {
      io.runsignal.WorkloadOptions.Builder b = io.runsignal.WorkloadOptions.builder();
      String cron = schedule != null ? schedule : fallbackSchedule;
      if (cron != null) b.schedule(cron);
      if (maxDuration != null) b.maxDuration(maxDuration);
      if (expectEvery != null) b.expectEvery(expectEvery);
      if (minProcessed > 0) b.minProcessed(minProcessed);
      if (failureRateThreshold > 0) b.failureRateThreshold(failureRateThreshold);
      if (severity != null) b.severity(severity);
      return b.build();
    }
  }

  public String getManifestPath() {
    return manifestPath;
  }

  public void setManifestPath(String manifestPath) {
    this.manifestPath = manifestPath;
  }

  public String getServiceName() {
    return serviceName;
  }

  public void setServiceName(String serviceName) {
    this.serviceName = serviceName;
  }

  public java.util.Map<String, Workload> getWorkloads() {
    return workloads;
  }

  public void setWorkloads(java.util.Map<String, Workload> workloads) {
    this.workloads = workloads;
  }

  public boolean isTracing() {
    return tracing;
  }

  public void setTracing(boolean tracing) {
    this.tracing = tracing;
  }
}
