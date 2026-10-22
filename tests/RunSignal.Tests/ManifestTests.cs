using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using RunSignal.Hangfire;
using RunSignal.Quartz;

namespace RunSignal.Tests;

public class ManifestTests
{
    [Fact]
    public void Manifest_lists_workloads_sorted_with_durations_in_seconds()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("stock-sync", o =>
        {
            o.ExpectEvery = TimeSpan.FromMinutes(5);
            o.FailureRateThreshold = 0.2;
        });
        registry.Get("nightly-invoice", o =>
        {
            o.Schedule = "0 2 * * *";
            o.MaxDuration = TimeSpan.FromMinutes(30);
            o.MinProcessed = 100;
            o.Severity = Severity.Critical;
        });

        var json = registry.CreateManifest("billing-service").ToJson();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("billing-service", root.GetProperty("service").GetString());
        var workloads = root.GetProperty("workloads").EnumerateArray().ToArray();
        Assert.Equal(["nightly-invoice", "stock-sync"], workloads.Select(w => w.GetProperty("name").GetString()));

        var invoice = workloads[0];
        Assert.Equal("0 2 * * *", invoice.GetProperty("schedule").GetString());
        Assert.Equal(1800, invoice.GetProperty("maxDurationSeconds").GetDouble());
        Assert.Equal(100, invoice.GetProperty("minProcessed").GetInt64());
        Assert.Equal("critical", invoice.GetProperty("severity").GetString());
        Assert.False(invoice.TryGetProperty("expectEverySeconds", out _)); // nulls are left out

        Assert.Equal(300, workloads[1].GetProperty("expectEverySeconds").GetDouble());
        Assert.Equal("warning", workloads[1].GetProperty("severity").GetString());
    }

    [Fact]
    public void Manifest_round_trips()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("nightly-invoice", o => o.Severity = Severity.Info);
        var manifest = registry.CreateManifest("billing");

        var read = WorkloadManifest.FromJson(manifest.ToJson());

        Assert.Equal("billing", read.Service);
        Assert.Equal(Severity.Info, Assert.Single(read.Workloads).Severity);
    }

    [Fact]
    public async Task Host_discovers_unrun_jobs_and_writes_the_manifest_on_startup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runsignal-{Guid.NewGuid():N}", "manifest.json");
        using var hangfireStorage = new InMemoryStorage();
        new RecurringJobManager(hangfireStorage).AddOrUpdate("Supplier Price Sync", Job.FromExpression(() => HangfireJobs.Succeeds()), "0 6 * * *");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<JobStorage>(hangfireStorage);
        builder.Services.AddQuartz(q => q.ScheduleJob<SucceedingJob>(
            t => t.WithCronSchedule("0 0 23 * * ?"),
            j => j.WithIdentity("MonthEndClose", "finance")));
        builder.Services.AddQuartzHostedService();
        builder.Services.AddRunSignal(o =>
            {
                o.ServiceName = "erp";
                o.ManifestPath = path;
            })
            .AddWorkload("stock-sync", o => o.ExpectEvery = TimeSpan.FromMinutes(5))
            .AddHangfire()
            .AddQuartz();

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        var manifest = WorkloadManifest.FromJson(await File.ReadAllTextAsync(path));
        Assert.Equal("erp", manifest.Service);
        Assert.Equal(["finance.month-end-close", "stock-sync", "supplier-price-sync"], manifest.Workloads.Select(w => w.Name));
        Assert.Equal("0 0 23 * * *", manifest.Workloads[0].Schedule);
        Assert.Equal("0 6 * * *", manifest.Workloads[2].Schedule);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public async Task Discovery_failure_does_not_stop_the_application()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddRunSignal().AddWorkload("still-here");
        builder.Services.AddSingleton<IWorkloadDiscovery, BrokenDiscovery>();

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        Assert.Single(host.Services.GetRequiredService<WorkloadRegistry>().Workloads);
    }

    private sealed class BrokenDiscovery : IWorkloadDiscovery
    {
        public Task DiscoverAsync(WorkloadRegistry registry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Storage is down.");
    }
}
