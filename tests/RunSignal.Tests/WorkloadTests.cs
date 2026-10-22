using System.Diagnostics;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.Tests;

public class WorkloadTests
{
    private static readonly WorkloadRegistry s_registry = new();

    [Fact]
    public void Succeed_records_run_duration_and_outcome()
    {
        var name = TestNames.Unique("succeed");
        using var recorder = new MetricRecorder(name);
        var workload = s_registry.Get(name);

        var run = workload.Start();
        run.Succeed();

        var runs = Assert.Single(recorder.Of(Instruments.Runs));
        Assert.Equal(1, runs.Value);
        Assert.Equal("success", runs.Tags[Attributes.Outcome]);
        Assert.False(runs.Tags.ContainsKey(Attributes.ErrorType));

        var duration = Assert.Single(recorder.Of(Instruments.Duration));
        Assert.True(duration.Value >= 0);
        Assert.Equal("success", duration.Tags[Attributes.Outcome]);
    }

    [Fact]
    public void Fail_with_exception_records_exception_type()
    {
        var name = TestNames.Unique("fail-ex");
        using var recorder = new MetricRecorder(name);

        s_registry.Get(name).Start().Fail(new TimeoutException("bank did not answer"));

        var runs = Assert.Single(recorder.Of(Instruments.Runs));
        Assert.Equal("failure", runs.Tags[Attributes.Outcome]);
        Assert.Equal("System.TimeoutException", runs.Tags[Attributes.ErrorType]);
    }

    [Fact]
    public void Fail_with_code_records_code_as_error_type()
    {
        var name = TestNames.Unique("fail-code");
        using var recorder = new MetricRecorder(name);

        s_registry.Get(name).Start().Fail("statement_missing", "No statement for 2026-10-23");

        var runs = Assert.Single(recorder.Of(Instruments.Runs));
        Assert.Equal("statement_missing", runs.Tags[Attributes.ErrorType]);
    }

    [Fact]
    public async Task RunAsync_succeeds_when_work_completes()
    {
        var name = TestNames.Unique("run-ok");
        using var recorder = new MetricRecorder(name);

        var result = await s_registry.Get(name).RunAsync(_ => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal("success", Assert.Single(recorder.Of(Instruments.Runs)).Tags[Attributes.Outcome]);
    }

    [Fact]
    public async Task RunAsync_fails_and_rethrows_when_work_throws()
    {
        var name = TestNames.Unique("run-fail");
        using var recorder = new MetricRecorder(name);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s_registry.Get(name).RunAsync(_ => throw new InvalidOperationException()));

        var runs = Assert.Single(recorder.Of(Instruments.Runs));
        Assert.Equal("failure", runs.Tags[Attributes.Outcome]);
        Assert.Equal("System.InvalidOperationException", runs.Tags[Attributes.ErrorType]);
    }

    [Fact]
    public async Task RunAsync_is_cancelled_not_failed_when_its_token_is_cancelled()
    {
        var name = TestNames.Unique("run-cancel");
        using var recorder = new MetricRecorder(name);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s_registry.Get(name).RunAsync(async ct =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }, cts.Token));

        Assert.Equal("cancelled", Assert.Single(recorder.Of(Instruments.Runs)).Tags[Attributes.Outcome]);
    }

    [Fact]
    public async Task RunAsync_fails_when_work_is_cancelled_by_another_token()
    {
        var name = TestNames.Unique("run-timeout");
        using var recorder = new MetricRecorder(name);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s_registry.Get(name).RunAsync(_ => Task.FromCanceled(new CancellationToken(canceled: true))));

        Assert.Equal("failure", Assert.Single(recorder.Of(Instruments.Runs)).Tags[Attributes.Outcome]);
    }

    [Fact]
    public void Items_and_heartbeats_are_counted()
    {
        var name = TestNames.Unique("items");
        using var recorder = new MetricRecorder(name);
        var workload = s_registry.Get(name);

        workload.Processed(10);
        workload.Processed(0);
        workload.ItemFailed();
        workload.Beat();

        var items = recorder.Of(Instruments.Items);
        Assert.Equal(10, items.Where(m => (string?)m.Tags[Attributes.ItemResult] == "processed").Sum(m => m.Value));
        Assert.Equal(1, items.Where(m => (string?)m.Tags[Attributes.ItemResult] == "failed").Sum(m => m.Value));
        Assert.Single(recorder.Of(Instruments.Heartbeats));
        Assert.Throws<ArgumentOutOfRangeException>(() => workload.Processed(-1));
    }

    [Fact]
    public void Since_success_is_reported_for_scheduled_workloads_before_their_first_run()
    {
        var name = TestNames.Unique("scheduled");
        using var recorder = new MetricRecorder(name);
        s_registry.Get(name, o => o.Schedule = "0 2 * * *");

        recorder.CollectObservable();

        // Counted from registration, so a job that never runs still looks overdue to the backend.
        Assert.InRange(Assert.Single(recorder.Of(Instruments.SinceSuccess)).Value, 0, 5);
        Assert.Empty(recorder.Of(Instruments.SinceHeartbeat));
    }

    [Fact]
    public void Since_success_is_not_reported_for_unscheduled_workloads_until_they_run()
    {
        var name = TestNames.Unique("unscheduled");
        using var recorder = new MetricRecorder(name);
        var workload = s_registry.Get(name);

        recorder.CollectObservable();
        Assert.Empty(recorder.Of(Instruments.SinceSuccess));

        workload.Start().Fail("boom");
        recorder.CollectObservable();
        Assert.Single(recorder.Of(Instruments.SinceSuccess));
    }

    [Fact]
    public async Task Since_success_resets_on_success_but_not_on_failure()
    {
        var name = TestNames.Unique("since-success");
        using var recorder = new MetricRecorder(name);
        var workload = s_registry.Get(name, o => o.Schedule = "* * * * *");
        await Task.Delay(300);

        workload.Start().Fail("boom");
        recorder.CollectObservable();
        Assert.True(recorder.Of(Instruments.SinceSuccess)[^1].Value >= 0.25);

        workload.Start().Succeed();
        recorder.CollectObservable();
        Assert.True(recorder.Of(Instruments.SinceSuccess)[^1].Value < 0.25);
    }

    [Fact]
    public async Task Since_heartbeat_resets_on_beat()
    {
        var name = TestNames.Unique("since-beat");
        using var recorder = new MetricRecorder(name);
        var workload = s_registry.Get(name, o => o.ExpectEvery = TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        recorder.CollectObservable();
        Assert.True(recorder.Of(Instruments.SinceHeartbeat)[^1].Value >= 0.25);

        workload.Beat();
        recorder.CollectObservable();
        Assert.True(recorder.Of(Instruments.SinceHeartbeat)[^1].Value < 0.25);
    }

    [Fact]
    public void Run_is_recorded_as_a_span_when_listened_to()
    {
        var name = TestNames.Unique("span");
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.OperationName == name) lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        s_registry.Get(name).Start().Fail(new TimeoutException("slow"));

        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(name, activity.GetTagItem(Attributes.Workload));
        Assert.Equal("System.TimeoutException", activity.GetTagItem(Attributes.ErrorType));
        Assert.Contains(activity.Events, e => e.Name == "exception");
    }

    [Fact]
    public void Default_run_is_a_no_op()
    {
        var run = default(WorkloadRun);
        run.Succeed();
        run.Fail("x");
        Assert.Null(run.Workload);
        Assert.Equal(TimeSpan.Zero, run.Elapsed);
    }
}
