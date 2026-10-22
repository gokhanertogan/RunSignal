using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RunSignal;

/// <summary>
/// Once every hosted service (including Hangfire and Quartz.NET) has started, discovers workloads that have not
/// run yet and, if asked to, writes the workload manifest. Failures are logged and never stop the application.
/// </summary>
internal sealed class RunSignalStartupService(
    WorkloadRegistry registry,
    IEnumerable<IWorkloadDiscovery> discoveries,
    IOptions<RunSignalOptions> options,
    ILogger<RunSignalStartupService> logger) : IHostedLifecycleService
{
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        foreach (var discovery in discoveries)
        {
            try
            {
                await discovery.DiscoverAsync(registry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "RunSignal could not discover workloads with {Discovery}.", discovery.GetType().Name);
            }
        }

        var path = options.Value.ManifestPath ?? Environment.GetEnvironmentVariable("RUNSIGNAL_MANIFEST_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var manifest = registry.CreateManifest(options.Value.ResolveServiceName());
            await WriteAtomicallyAsync(path, manifest.ToJson(), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("RunSignal wrote a manifest of {Count} workloads to {Path}.", manifest.Workloads.Count, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RunSignal could not write the workload manifest to {Path}.", path);
        }
    }

    // Write to a temporary file and move it into place, so a reader never sees half a manifest.
    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, fullPath, overwrite: true);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
