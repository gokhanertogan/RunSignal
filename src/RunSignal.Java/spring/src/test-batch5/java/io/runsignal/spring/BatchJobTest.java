// Spring Batch 5 (Spring Boot 3) version of src/test-batch6: the same jobs and assertions, older package names.
package io.runsignal.spring;

import static org.assertj.core.api.Assertions.assertThat;

import io.opentelemetry.api.OpenTelemetry;
import io.opentelemetry.sdk.OpenTelemetrySdk;
import io.opentelemetry.sdk.metrics.SdkMeterProvider;
import io.opentelemetry.sdk.metrics.data.MetricData;
import io.opentelemetry.sdk.testing.exporter.InMemoryMetricReader;
import io.runsignal.Severity;
import io.runsignal.WorkloadRegistry;
import java.time.Duration;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.springframework.batch.core.Job;
import org.springframework.batch.core.JobParametersBuilder;
import org.springframework.batch.core.job.builder.JobBuilder;
import org.springframework.batch.core.launch.JobLauncher;
import org.springframework.batch.core.repository.JobRepository;
import org.springframework.batch.core.step.builder.StepBuilder;
import org.springframework.batch.item.support.ListItemReader;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.boot.SpringBootConfiguration;
import org.springframework.boot.autoconfigure.EnableAutoConfiguration;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.annotation.Bean;
import org.springframework.transaction.PlatformTransactionManager;

@SpringBootTest(classes = BatchJobTest.App.class, properties = {
    "spring.batch.job.enabled=false",
    "runsignal.workloads.invoice-export.max-duration=30m",
    "runsignal.workloads.invoice-export.min-processed=5",
    "runsignal.workloads.invoice-export.severity=critical",
    "runsignal.workloads.invoice-export.owner=billing-team",
    "runsignal.workloads.invoice-export.runbook-url=https://wiki.example.com/invoice-export",
})
class BatchJobTest {
  static {
    // A run whose afterJob never comes is ended this long after its execution ended (30 s in production).
    BatchJobListener.sweepGrace = Duration.ofMillis(300);
    BatchJobListener.sweepInterval = Duration.ofMillis(50);
  }

  @SpringBootConfiguration
  @EnableAutoConfiguration
  static class App {
    @Bean
    InMemoryMetricReader reader() {
      return InMemoryMetricReader.create();
    }

    @Bean
    OpenTelemetry openTelemetry(InMemoryMetricReader reader) {
      return OpenTelemetrySdk.builder().setMeterProvider(SdkMeterProvider.builder().registerMetricReader(reader).build()).build();
    }

    // Reads 10 invoices, skips the 2 that cannot be exported.
    @Bean
    Job invoiceExport(JobRepository repository, PlatformTransactionManager tx) {
      var step = new StepBuilder("export", repository)
          .<Integer, Integer>chunk(3, tx)
          .reader(new ListItemReader<>(List.of(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)))
          .processor(i -> {
            if (i % 5 == 0) throw new IllegalArgumentException("invoice " + i + " has no customer");
            return i;
          })
          .writer(chunk -> { })
          .faultTolerant()
          .skip(IllegalArgumentException.class)
          .skipLimit(10)
          .build();
      return new JobBuilder("invoiceExport", repository).start(step).build();
    }

    @Bean
    Job ledgerClose(JobRepository repository, PlatformTransactionManager tx) {
      var step = new StepBuilder("close", repository)
          .tasklet((contribution, context) -> {
            throw new IllegalStateException("ledger is locked");
          }, tx)
          .build();
      return new JobBuilder("ledgerClose", repository).start(step).build();
    }

    // Its job repository fails to save the finished execution, so Spring Batch never calls afterJob.
    @Bean
    Job catalogSync(JobRepository repository, PlatformTransactionManager tx) {
      var step = new StepBuilder("sync", repository).tasklet((contribution, context) -> null, tx).build();
      JobRepository failing = (JobRepository) java.lang.reflect.Proxy.newProxyInstance(
          JobRepository.class.getClassLoader(), new Class<?>[] {JobRepository.class}, (proxy, method, args) -> {
            if (method.getName().equals("update") && args[0].getClass().getSimpleName().equals("JobExecution")
                && args[0].getClass().getMethod("getEndTime").invoke(args[0]) != null) {
              throw new IllegalStateException("database is down");
            }
            try {
              return method.invoke(repository, args);
            } catch (java.lang.reflect.InvocationTargetException e) {
              throw e.getCause();
            }
          });
      return new JobBuilder("catalogSync", failing).start(step).build();
    }
  }

  @Autowired JobLauncher operator;
  @Autowired @Qualifier("invoiceExport") Job invoiceExport;
  @Autowired @Qualifier("ledgerClose") Job ledgerClose;
  @Autowired @Qualifier("catalogSync") Job catalogSync;
  @Autowired WorkloadRegistry registry;
  @Autowired InMemoryMetricReader reader;

  @Test
  void jobsAreRecordedWithTheirItemsAndConfiguredExpectations() throws Exception {
    // Registered as workloads before they ever run.
    assertThat(registry.workloads()).extracting(w -> w.name()).contains("invoice-export", "ledger-close");
    var export = registry.workload("invoice-export");
    assertThat(export.options().maxDuration()).contains(Duration.ofMinutes(30));
    assertThat(export.options().minProcessed()).hasValue(5);
    assertThat(export.options().severity()).isEqualTo(Severity.CRITICAL);
    assertThat(export.options().owner()).contains("billing-team");
    assertThat(export.options().runbookUrl()).contains("https://wiki.example.com/invoice-export");
    assertThat(registry.workload("ledger-close").options().owner()).isEmpty();

    operator.run(invoiceExport, new JobParametersBuilder().addLong("run", 1L).toJobParameters());
    operator.run(ledgerClose, new JobParametersBuilder().addLong("run", 1L).toJobParameters());

    Map<String, Long> sums = new HashMap<>();
    for (MetricData metric : reader.collectAllMetrics()) {
      if (metric.getName().equals("runsignal.workload.runs") || metric.getName().equals("runsignal.workload.items")) {
        metric.getLongSumData().getPoints().forEach(p -> sums.merge(metric.getName() + " " + p.getAttributes().asMap().values().stream()
            .map(String::valueOf).sorted().toList(), p.getValue(), Long::sum));
      }
    }
    assertThat(sums).containsEntry("runsignal.workload.runs [invoice-export, success]", 1L)
        .containsEntry("runsignal.workload.items [invoice-export, processed]", 8L)
        .containsEntry("runsignal.workload.items [failed, invoice-export]", 2L)
        .containsEntry("runsignal.workload.runs [failure, java.lang.IllegalStateException, ledger-close]", 1L);
  }

  @Test
  void aRunWhoseFinalRepositoryUpdateFailsIsEndedInsteadOfLookingHung() throws Exception {
    // Spring Batch 5 calls afterJob before the final repository update, so the run ends normally there; Batch 6
    // calls it after, and the sweep ends the run instead (see src/test-batch6).
    try {
      operator.run(catalogSync, new JobParametersBuilder().addLong("run", 1L).toJobParameters());
    } catch (Exception expected) {
      // the failed update may propagate to the caller
    }

    Long failed = null;
    Double age = null;
    long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
    while ((failed == null || age == null || age != 0.0) && System.nanoTime() < deadline) {
      Thread.sleep(50);
      failed = null;
      age = null;
      for (MetricData metric : reader.collectAllMetrics()) {
        if (metric.getName().equals("runsignal.workload.runs")) {
          for (var p : metric.getLongSumData().getPoints()) {
            if (p.getAttributes().asMap().values().stream().map(String::valueOf).sorted().toList()
                .equals(List.of("catalog-sync", "success"))) {
              failed = p.getValue();
            }
          }
        }
        if (metric.getName().equals("runsignal.workload.active_run_age")) {
          for (var p : metric.getDoubleGaugeData().getPoints()) {
            if (p.getAttributes().asMap().containsValue("catalog-sync")) {
              age = p.getValue();
            }
          }
        }
      }
    }
    assertThat(failed).isEqualTo(1L);
    assertThat(age).isZero();
  }
}
