using Microsoft.Extensions.Logging.Abstractions;

namespace RunSignal.Tests;

/// <summary>
/// Quartz.NET 3 keeps its logger factory in a static. A host that registers Quartz stores its own factory there,
/// and once that host is disposed every later scheduler in the process fails with ObjectDisposedException.
/// Tests that use Quartz run in this collection, one at a time, and reset the static before they start.
/// </summary>
[CollectionDefinition(Name)]
public sealed class QuartzIsolation
{
    public const string Name = "Quartz";

    public static void ResetLogging() => global::Quartz.Logging.LogContext.SetCurrentLogProvider(NullLoggerFactory.Instance);
}
