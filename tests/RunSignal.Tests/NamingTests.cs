namespace RunSignal.Tests;

public class NamingTests
{
    // Shared with every SDK, so a job gets the same name in every language.
    public static TheoryData<string, string> ConformanceNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in Conformance.Load("workload-names.json").GetProperty("names").EnumerateArray())
            data.Add(name.GetProperty("input").GetString()!, name.GetProperty("expected").GetString()!);
        return data;
    }

    public static TheoryData<string> ConformanceInvalidNames()
    {
        var data = new TheoryData<string>();
        foreach (var input in Conformance.Load("workload-names.json").GetProperty("invalid").EnumerateArray())
            data.Add(input.GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(ConformanceNames))]
    public void ToWorkloadName_matches_the_conformance_vectors(string identifier, string expected)
    {
        Assert.Equal(expected, WorkloadRegistry.ToWorkloadName(identifier));
        using var registry = new WorkloadRegistry();
        registry.Get(expected); // must be accepted
    }

    [Theory]
    [MemberData(nameof(ConformanceInvalidNames))]
    public void ToWorkloadName_rejects_the_conformance_invalid_inputs(string identifier) =>
        Assert.Throws<ArgumentException>(() => WorkloadRegistry.ToWorkloadName(identifier));

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
