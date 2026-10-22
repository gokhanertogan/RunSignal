using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using RunSignal.Quartz;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.Tests;

public sealed class SucceedingJob : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        context.GetWorkload()?.Processed(7);
        return Task.CompletedTask;
    }
}

public sealed class FailingJob : IJob
{
    public Task Execute(IJobExecutionContext context) => throw new InvalidOperationException("Ledger is locked.");
}

public sealed class AnonymousJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

[Workload("quartz-attributed", Schedule = "0 0 4 * * ?", ExpectEvery = "10m")]
public sealed class AttributedJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}

public sealed class QuartzTests : IAsyncLifetime
{
    private readonly WorkloadRegistry _registry = new();
    private IScheduler _scheduler = null!;

    public async Task InitializeAsync()
    {
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = TestNames.Unique("scheduler"),
            ["quartz.threadPool.maxConcurrency"] = "2",
        });
        _scheduler = await factory.GetScheduler();
        _scheduler.UseRunSignal(_registry);
        await _scheduler.Start();
    }

    public async Task DisposeAsync()
    {
        await _scheduler.Shutdown(waitForJobsToComplete: false);
        _registry.Dispose();
    }

    [Fact]
    public async Task Job_is_named_after_its_key_and_can_count_items()
    {
        var key = new JobKey(TestNames.Unique("DailySales"), "reports");
        var name = WorkloadRegistry.ToWorkloadName($"reports.{key.Name}");
        using var recorder = new MetricRecorder(name);

        await _scheduler.ScheduleJob(JobBuilder.Create<SucceedingJob>().WithIdentity(key).Build(), TriggerBuilder.Create().StartNow().Build());

        var run = await recorder.WaitForAsync(Instruments.Runs);
        Assert.Equal("success", run.Tags[Attributes.Outcome]);
        Assert.Equal(7, recorder.Of(Instruments.Items).Sum(m => m.Value));
    }

    [Fact]
    public async Task Job_without_identity_is_named_after_its_class()
    {
        using var recorder = new MetricRecorder("anonymous-job");

        await _scheduler.ScheduleJob(JobBuilder.Create<AnonymousJob>().Build(), TriggerBuilder.Create().StartNow().Build());

        await recorder.WaitForAsync(Instruments.Runs);
        Assert.Contains(_registry.Workloads, w => w.Name == "anonymous-job");
    }

    [Fact]
    public async Task Cron_trigger_becomes_the_schedule()
    {
        var key = new JobKey(TestNames.Unique("cron"));
        var name = WorkloadRegistry.ToWorkloadName(key.Name);
        using var recorder = new MetricRecorder(name);

        await _scheduler.ScheduleJob(
            JobBuilder.Create<SucceedingJob>().WithIdentity(key).Build(),
            TriggerBuilder.Create().WithCronSchedule("0/1 * * * * ?").Build());

        await recorder.WaitForAsync(Instruments.Runs);
        Assert.Equal("0/1 * * * * *", _registry.Get(name).Options.Schedule); // converted from Quartz format
    }

    [Theory]
    [InlineData("0 0 4 * * ?", "0 0 4 * * *")]
    [InlineData("0 0 9 ? * 2-6", "0 0 9 * * 1-5")]          // Quartz counts SUN as 1
    [InlineData("0 0 9 ? * 1,7", "0 0 9 * * 0,6")]
    [InlineData("0 0 9 ? * 6#3", "0 0 9 * * 5#3")]          // third Friday: only the day shifts
    [InlineData("0 0 9 ? * 6L", "0 0 9 * * 5L")]            // last Friday
    [InlineData("0 0 9 ? * MON-FRI", "0 0 9 * * MON-FRI")]
    [InlineData("0/30 * * * * ? 2026", "0/30 * * * * *")]   // the year is dropped
    [InlineData("0 2 * * *", "0 2 * * *")]                  // not Quartz: unchanged
    public void Quartz_crons_are_converted_to_the_standard_format(string quartz, string standard) =>
        Assert.Equal(standard, QuartzCron.ToStandard(quartz));

    [Fact]
    public async Task Failed_job_records_the_original_exception_type()
    {
        var key = new JobKey(TestNames.Unique("failing"));
        using var recorder = new MetricRecorder(WorkloadRegistry.ToWorkloadName(key.Name));

        await _scheduler.ScheduleJob(JobBuilder.Create<FailingJob>().WithIdentity(key).Build(), TriggerBuilder.Create().StartNow().Build());

        var run = await recorder.WaitForAsync(Instruments.Runs);
        Assert.Equal("failure", run.Tags[Attributes.Outcome]);
        Assert.Equal("System.InvalidOperationException", run.Tags[Attributes.ErrorType]);
    }

    [Fact]
    public async Task Attribute_names_the_workload_and_sets_expectations()
    {
        using var recorder = new MetricRecorder("quartz-attributed");

        await _scheduler.ScheduleJob(JobBuilder.Create<AttributedJob>().Build(), TriggerBuilder.Create().StartNow().Build());

        await recorder.WaitForAsync(Instruments.Runs);
        var options = _registry.Get("quartz-attributed").Options;
        Assert.Equal("0 0 4 * * ?", options.Schedule);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ExpectEvery);
    }
}
