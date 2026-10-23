using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using RunSignal.Quartz;
using RunSignal.Tests;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.Quartz4.Tests;

public sealed class CountingJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        context.GetWorkload()?.Processed(7);
        return ValueTask.CompletedTask;
    }
}

public sealed class FailingJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Ledger is locked.");
}

[Workload("q4-attributed", MaxDuration = "5m")]
public sealed class NightlyJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class AnonymousJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public class Quartz4Tests
{
    // A real host with Quartz 4's own DI integration, as an application would set it up.
    private static async Task<IHost> StartAsync(Action<IQuartzBuilder> jobs, string scheduler = "main")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddRunSignal().AddQuartz();
        builder.Services.AddQuartz(scheduler, q =>
        {
            q.AddJobListener<RunSignalJobListener>([GroupMatcher<JobKey>.AnyGroup()]);
            jobs(q);
        });
        builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = false);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Jobs_are_recorded_and_can_count_items()
    {
        var name = TestNames.Unique("Counting");
        using var recorder = new MetricRecorder(WorkloadRegistry.ToWorkloadName(name));
        using var host = await StartAsync(q => q.ScheduleJob<CountingJob>(t => t.StartNow(), j => j.WithIdentity(name)));

        var run = await recorder.WaitForAsync(Instruments.Runs);
        await host.StopAsync();

        Assert.Equal("success", run.Tags[Attributes.Outcome]);
        Assert.Equal(7, recorder.Of(Instruments.Items).Sum(m => m.Value));
    }

    [Fact]
    public async Task Failures_report_the_job_exception()
    {
        var name = TestNames.Unique("Failing");
        using var recorder = new MetricRecorder(WorkloadRegistry.ToWorkloadName(name));
        using var host = await StartAsync(q => q.ScheduleJob<FailingJob>(t => t.StartNow(), j => j.WithIdentity(name)));

        var run = await recorder.WaitForAsync(Instruments.Runs);
        await host.StopAsync();

        Assert.Equal("failure", run.Tags[Attributes.Outcome]);
        Assert.Equal("System.InvalidOperationException", run.Tags[Attributes.ErrorType]);
    }

    [Fact]
    public async Task Cron_jobs_are_discovered_with_a_converted_schedule_and_attributes_apply()
    {
        using var host = await StartAsync(q =>
        {
            q.ScheduleJob<NightlyJob>(t => t.WithCronSchedule("0 0 9 ? * 2-6"), j => j.WithIdentity("nightly"));
            q.ScheduleJob<AnonymousJob>(t => t.WithCronSchedule("0 0 3 * * ?"));
        });
        var registry = host.Services.GetRequiredService<WorkloadRegistry>();
        await host.StopAsync();

        var nightly = registry.Get("q4-attributed");
        Assert.Equal("0 0 9 * * 1-5", nightly.Options.Schedule); // Quartz counts SUN as 1
        Assert.Equal(TimeSpan.FromMinutes(5), nightly.Options.MaxDuration);
        Assert.Equal("0 0 3 * * *", registry.Get("anonymous-job").Options.Schedule); // no identity: named after its class
    }

    [Fact]
    public async Task Runs_of_two_schedulers_do_not_mix()
    {
        var first = TestNames.Unique("First");
        var second = TestNames.Unique("Second");
        using var firstRecorder = new MetricRecorder(WorkloadRegistry.ToWorkloadName(first));
        using var secondRecorder = new MetricRecorder(WorkloadRegistry.ToWorkloadName(second));

        // Both schedulers use the instance ID NON_CLUSTERED, so their fire instance IDs can be equal.
        using var a = await StartAsync(q => q.ScheduleJob<CountingJob>(t => t.StartNow(), j => j.WithIdentity(first)), "a");
        using var b = await StartAsync(q => q.ScheduleJob<FailingJob>(t => t.StartNow(), j => j.WithIdentity(second)), "b");

        Assert.Equal("success", (await firstRecorder.WaitForAsync(Instruments.Runs)).Tags[Attributes.Outcome]);
        Assert.Equal("failure", (await secondRecorder.WaitForAsync(Instruments.Runs)).Tags[Attributes.Outcome]);
        await a.StopAsync();
        await b.StopAsync();
    }
}
