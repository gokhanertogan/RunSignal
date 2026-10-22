package io.runsignal.spring;

import io.micrometer.observation.Observation;
import io.micrometer.observation.ObservationHandler;
import io.opentelemetry.context.Scope;
import io.runsignal.WorkloadRun;
import org.springframework.scheduling.support.ScheduledTaskObservationContext;

/**
 * Records every {@code @Scheduled} method run, through Spring's own scheduling observations, as a run of its
 * workload. No proxies are involved, so final classes and self-invocation work.
 */
public final class RunSignalObservationHandler implements ObservationHandler<ScheduledTaskObservationContext> {
  private final ScheduledWorkloads workloads;

  public RunSignalObservationHandler(ScheduledWorkloads workloads) {
    this.workloads = workloads;
  }

  @Override
  public boolean supportsContext(Observation.Context context) {
    return context instanceof ScheduledTaskObservationContext;
  }

  @Override
  public void onStart(ScheduledTaskObservationContext context) {
    WorkloadRun run = workloads.workloadFor(context.getTargetClass(), context.getMethod(), null).start();
    context.put(WorkloadRun.class, run);
  }

  @Override
  public void onScopeOpened(ScheduledTaskObservationContext context) {
    WorkloadRun run = context.get(WorkloadRun.class);
    if (run != null && run.span() != null) {
      context.put(Scope.class, run.span().makeCurrent());
    }
  }

  @Override
  public void onScopeClosed(ScheduledTaskObservationContext context) {
    Scope scope = (Scope) context.remove(Scope.class);
    if (scope != null) {
      scope.close();
    }
  }

  @Override
  public void onStop(ScheduledTaskObservationContext context) {
    WorkloadRun run = (WorkloadRun) context.remove(WorkloadRun.class);
    if (run == null) {
      return;
    }
    Throwable error = context.getError();
    if (error == null) {
      run.succeed();
    } else if (error instanceof InterruptedException) {
      run.cancel();
    } else {
      run.fail(error);
    }
  }
}
