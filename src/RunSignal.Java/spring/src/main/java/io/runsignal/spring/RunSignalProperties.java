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

  public boolean isTracing() {
    return tracing;
  }

  public void setTracing(boolean tracing) {
    this.tracing = tracing;
  }
}
