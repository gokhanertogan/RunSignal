namespace RunSignal.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("InvoiceJobs.GenerateInvoices", "invoice-jobs.generate-invoices")]
    [InlineData("HTTPSync", "http-sync")]
    [InlineData("Reports/Daily Sales", "reports-daily-sales")]
    [InlineData("nightly-invoice", "nightly-invoice")]
    [InlineData("  e-Arşiv gönderimi ", "e-arsiv-gonderimi")]
    [InlineData("Günlük İrsaliye Işlemi", "gunluk-irsaliye-islemi")]
    [InlineData("Job2Run", "job2-run")]
    public void ToWorkloadName_produces_valid_names(string identifier, string expected)
    {
        Assert.Equal(expected, WorkloadRegistry.ToWorkloadName(identifier));
        using var registry = new WorkloadRegistry();
        registry.Get(expected); // must be accepted
    }

    [Fact]
    public void ToWorkloadName_rejects_identifiers_without_letters_or_digits() =>
        Assert.Throws<ArgumentException>(() => WorkloadRegistry.ToWorkloadName("///"));

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("1.5h", 5400)]
    [InlineData("1d", 86400)]
    [InlineData("00:30:00", 1800)]
    public void Attribute_durations_are_parsed(string value, double seconds)
    {
        var options = new WorkloadOptions();
        new WorkloadAttribute { MaxDuration = value }.ApplyTo(options);
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.MaxDuration);
    }

    [Fact]
    public void Invalid_attribute_duration_throws() =>
        Assert.Throws<FormatException>(() => new WorkloadAttribute { MaxDuration = "soon" }.ApplyTo(new WorkloadOptions()));

    [Fact]
    public void GetOrAdd_keeps_options_declared_first()
    {
        using var registry = new WorkloadRegistry();
        registry.Get("nightly-invoice", o => o.Severity = Severity.Critical);

        var workload = registry.GetOrAdd("nightly-invoice", o => o.Severity = Severity.Info);

        Assert.Equal(Severity.Critical, workload.Options.Severity);
    }
}
