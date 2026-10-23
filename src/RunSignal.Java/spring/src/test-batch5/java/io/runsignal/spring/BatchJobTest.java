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
})
class BatchJobTest {
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
  }

  @Autowired JobLauncher operator;
  @Autowired @Qualifier("invoiceExport") Job invoiceExport;
  @Autowired @Qualifier("ledgerClose") Job ledgerClose;
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
}
