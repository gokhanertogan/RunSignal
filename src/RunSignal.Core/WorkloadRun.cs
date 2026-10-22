using System.Diagnostics;

namespace RunSignal;

/// <summary>
/// One run of a <see cref="Workload"/>, started with <see cref="Workload.Start"/>.
/// End it with exactly one call to <see cref="Succeed"/>, <see cref="Fail(Exception)"/>,
/// <see cref="Fail(string, string?)"/> or <see cref="Cancel"/>.
/// </summary>
/// <remarks>
/// This is a struct so that starting a run does not allocate. Ending a run more than once records it more than once.
/// A run that is never ended records nothing, which the backend sees as a missed run.
/// Prefer <see cref="Workload.RunAsync(Func{CancellationToken, Task}, CancellationToken)"/>, which ends the run for you.
/// </remarks>
public readonly struct WorkloadRun
{
    private readonly Workload? _workload;
    private readonly long _startTimestamp;
    private readonly Activity? _activity;

    internal WorkloadRun(Workload workload, long startTimestamp, Activity? activity)
    {
        _workload = workload;
        _startTimestamp = startTimestamp;
        _activity = activity;
    }

    /// <summary>The workload this run belongs to, or <see langword="null"/> for a default instance.</summary>
    public Workload? Workload => _workload;

    /// <summary>Time since the run started.</summary>
    public TimeSpan Elapsed => _workload is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_startTimestamp);

    /// <summary>Ends the run as successful.</summary>
    public void Succeed() =>
        _workload?.Complete(_startTimestamp, _activity, Outcome.Success, errorType: null, exception: null, description: null);

    /// <summary>Ends the run as failed because of <paramref name="exception"/>.</summary>
    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _workload?.Complete(_startTimestamp, _activity, Outcome.Failure, exception.GetType().FullName, exception, exception.Message);
    }

    /// <summary>Ends the run as failed without an exception, for example when an expected input file is missing.</summary>
    /// <param name="errorType">A short, stable code such as <c>statement_missing</c>. Never put messages or IDs here.</param>
    /// <param name="description">Optional human-readable detail, recorded on the span only.</param>
    public void Fail(string errorType, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        _workload?.Complete(_startTimestamp, _activity, Outcome.Failure, errorType, exception: null, description);
    }

    /// <summary>Ends the run as cancelled, for example on shutdown. Cancelled runs do not count as failures.</summary>
    public void Cancel() =>
        _workload?.Complete(_startTimestamp, _activity, Outcome.Cancelled, errorType: null, exception: null, description: null);
}

internal enum Outcome
{
    Success,
    Failure,
    Cancelled,
}
