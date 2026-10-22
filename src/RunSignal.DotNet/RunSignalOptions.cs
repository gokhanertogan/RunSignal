namespace RunSignal;

/// <summary>Settings for <see cref="RunSignalServiceCollectionExtensions.AddRunSignal(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{RunSignalOptions})"/>.</summary>
public sealed class RunSignalOptions
{
    /// <summary>
    /// The service name written to the manifest. It must match the OpenTelemetry <c>service.name</c> the signals
    /// are exported with. Defaults to the <c>OTEL_SERVICE_NAME</c> environment variable, then the entry assembly name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Where to write the workload manifest once the application has started and discovered its workloads,
    /// or <see langword="null"/> (the default) to not write one. Defaults to the <c>RUNSIGNAL_MANIFEST_PATH</c>
    /// environment variable, so CI can ask for a manifest without code changes.
    /// </summary>
    public string? ManifestPath { get; set; }

    internal string ResolveServiceName() =>
        ServiceName
        ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
        ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
        ?? "unknown_service";
}
