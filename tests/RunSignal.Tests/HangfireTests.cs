using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using RunSignal.Hangfire;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.Tests;

public static class HangfireJobs
{
    public static void Succeeds() { }

    public static void ThrowsTimeout() => throw new TimeoutException("Supplier API did not answer.");

    [Workload("hf-attributed", MaxDuration = "5m", MinProcessed = 3, Severity = Severity.Critical)]
    public static void Attributed(PerformContext? context = null) => context.GetWorkload()?.Processed(3);
}

public sealed class HangfireTests : IDisposable
{
    private readonly WorkloadRegistry _registry = new();
    private readonly InMemoryStorage _storage = new();
    private readonly BackgroundJobServer _server;

    public HangfireTests()
    {
        var filters = new JobFilterCollection { new RunSignalHangfireFilter(_registry) };
        _server = new BackgroundJobServer(
            new BackgroundJobServerOptions { WorkerCount = 2, FilterProvider = filters, SchedulePollingInterval = TimeSpan.FromMilliseconds(100) },
            _storage);
    }

    public void Dispose()
    {
        _server.Dispose();
        _storage.Dispose();
        _registry.Dispose();
    }

    [Fact]
    public async Task Recurring_job_is_named_after_its_id_and_takes_its_cron_as_schedule()
    {
        var id = TestNames.Unique("Nightly Invoice");
        var name = WorkloadRegistry.ToWorkloadName(id);
        using var recorder = new MetricRecorder(name);

        var manager = new RecurringJobManager(_storage);
        manager.AddOrUpdate(id, Job.FromExpression(() => HangfireJobs.Succeeds()), "0 3 * * *");
        manager.Trigger(id);

        var run = await recorder.WaitForAsync(Instruments.Runs);
        Assert.Equal("success", run.Tags[Attributes.Outcome]);
        Assert.Equal("0 3 * * *", _registry.Get(name).Options.Schedule);
    }

    [Fact]
    public async Task Failed_job_records_the_original_exception_type()
    {
        using var recorder = new MetricRecorder("hangfire-jobs.throws-timeout");

        new BackgroundJobClient(_storage).Enqueue(() => HangfireJobs.ThrowsTimeout());

        var run = await recorder.WaitForAsync(Instruments.Runs);
        Assert.Equal("failure", run.Tags[Attributes.Outcome]);
        Assert.Equal("System.TimeoutException", run.Tags[Attributes.ErrorType]);
    }

    [Fact]
    public async Task Attribute_names_the_workload_and_job_can_count_items()
    {
        using var recorder = new MetricRecorder("hf-attributed");

        new BackgroundJobClient(_storage).Enqueue(() => HangfireJobs.Attributed(null));

        await recorder.WaitForAsync(Instruments.Runs);
        Assert.Equal(3, recorder.Of(Instruments.Items).Sum(m => m.Value));
        var options = _registry.Get("hf-attributed").Options;
        Assert.Equal(TimeSpan.FromMinutes(5), options.MaxDuration);
        Assert.Equal(3, options.MinProcessed);
        Assert.Equal(Severity.Critical, options.Severity);
    }
}
