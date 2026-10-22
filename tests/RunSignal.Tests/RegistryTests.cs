using Microsoft.Extensions.DependencyInjection;

namespace RunSignal.Tests;

public class RegistryTests
{
    [Fact]
    public void Get_returns_the_same_workload_for_the_same_name()
    {
        using var registry = new WorkloadRegistry();
        Assert.Same(registry.Get("sync"), registry.Get("sync"));
    }

    [Fact]
    public void Get_applies_options_on_creation()
    {
        using var registry = new WorkloadRegistry();
        var workload = registry.Get("nightly-invoice", o =>
        {
            o.Schedule = "0 2 * * *";
            o.MaxDuration = TimeSpan.FromMinutes(30);
            o.Severity = Severity.Critical;
        });

        Assert.Equal("0 2 * * *", workload.Options.Schedule);
        Assert.Equal(TimeSpan.FromMinutes(30), workload.Options.MaxDuration);
        Assert.Equal(Severity.Critical, workload.Options.Severity);
    }

    [Fact]
    public void Configuring_an_existing_workload_throws()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("sync");
        Assert.Throws<InvalidOperationException>(() => registry.Get("sync", o => o.Severity = Severity.Info));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nightly")]
    [InlineData("has space")]
    [InlineData("-starts-with-dash")]
    [InlineData("order/{id}")]
    public void Invalid_names_are_rejected(string name)
    {
        using var registry = new WorkloadRegistry();
        Assert.Throws<ArgumentException>(() => registry.Get(name));
    }

    [Fact]
    public void Failure_rate_threshold_must_be_a_share()
    {
        var options = new WorkloadOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.FailureRateThreshold = 1.5);
    }

    [Fact]
    public void AddWorkload_registers_keyed_workloads_and_creates_them_eagerly()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddRunSignal()
            .AddWorkload("stock-sync", o => o.ExpectEvery = TimeSpan.FromMinutes(5))
            .AddWorkload("nightly-invoice");
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<WorkloadRegistry>();
        Assert.Equal(["nightly-invoice", "stock-sync"], registry.Workloads.Select(w => w.Name).Order());

        var stockSync = provider.GetRequiredKeyedService<Workload>("stock-sync");
        Assert.Same(registry.Get("stock-sync"), stockSync);
        Assert.Equal(TimeSpan.FromMinutes(5), stockSync.Options.ExpectEvery);
    }
}
