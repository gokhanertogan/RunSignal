using System.Diagnostics;

namespace RunSignal;

/// <summary>
/// A unit of background work, such as a scheduled job, a queue consumer or a sync loop.
/// Get one from <see cref="WorkloadRegistry.Get"/> once and keep it: every recording method is thread-safe
/// and does not allocate when no span is being recorded.
/// </summary>
public sealed class Workload
{
    private static readonly KeyValuePair<string, object?> s_success = new(RunSignalTelemetry.Attributes.Outcome, "success");
    private static readonly KeyValuePair<string, object?> s_failure = new(RunSignalTelemetry.Attributes.Outcome, "failure");
    private static readonly KeyValuePair<string, object?> s_cancelled = new(RunSignalTelemetry.Attributes.Outcome, "cancelled");
    private static readonly KeyValuePair<string, object?> s_processed = new(RunSignalTelemetry.Attributes.ItemResult, "processed");
    private static readonly KeyValuePair<string, object?> s_failed = new(RunSignalTelemetry.Attributes.ItemResult, "failed");

    private readonly WorkloadInstruments _instruments;
    private readonly KeyValuePair<string, object?> _nameTag;

    // Stopwatch timestamps. Until the first success or heartbeat they hold the time the workload was created, so a
    // workload that never succeeds or never beats still reports a growing age, which the backend alerts on.
    private long _lastSuccessTimestamp;
    private long _lastHeartbeatTimestamp;
    private bool _hasStarted;
    private bool _hasBeaten;

    internal Workload(string name, WorkloadOptions options, WorkloadInstruments instruments)
    {
        Name = name;
        Options = options;
        _instruments = instruments;
        _nameTag = new(RunSignalTelemetry.Attributes.Workload, name);
        _lastSuccessTimestamp = _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>The workload's stable name, such as <c>nightly-invoice</c>.</summary>
    public string Name { get; }

    /// <summary>What the workload is expected to do, used to provision alerts.</summary>
    public WorkloadOptions Options { get; }

    /// <summary>Starts a run. End it with <see cref="WorkloadRun.Succeed"/>, <see cref="WorkloadRun.Fail(Exception)"/> or <see cref="WorkloadRun.Cancel"/>.</summary>
    public WorkloadRun Start()
    {
        var activity = WorkloadInstruments.ActivitySource.StartActivity(Name, ActivityKind.Internal);
        activity?.SetTag(RunSignalTelemetry.Attributes.Workload, Name);
        if (!_hasStarted)
            _hasStarted = true;
        return new WorkloadRun(this, Stopwatch.GetTimestamp(), activity);
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one run of this workload: it succeeds when <paramref name="work"/> completes,
    /// fails when it throws, and is cancelled when it throws because <paramref name="cancellationToken"/> was cancelled.
    /// Exceptions are rethrown.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var run = Start();
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            throw;
        }
        run.Succeed();
    }

    /// <inheritdoc cref="RunAsync(Func{CancellationToken, Task}, CancellationToken)"/>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var run = Start();
        T result;
        try
        {
            result = await work(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            run.Fail(ex);
            throw;
        }
        run.Succeed();
        return result;
    }

    /// <summary>Records <paramref name="count"/> successfully processed items. Recording 0 is useful: it reports that a run did nothing.</summary>
    public void Processed(long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _instruments.Items.Add(count, _nameTag, s_processed);
    }

    /// <summary>Records <paramref name="count"/> items that could not be processed.</summary>
    public void ItemFailed(long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _instruments.Items.Add(count, _nameTag, s_failed);
    }

    /// <summary>Records a heartbeat: the workload is alive and making progress.</summary>
    public void Beat()
    {
        Volatile.Write(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
        if (!_hasBeaten)
            _hasBeaten = true;
        _instruments.Heartbeats.Add(1, _nameTag);
    }

    // Reported for workloads that are scheduled or have run, so alerts can tell a missed run from a workload
    // that simply has no runs.
    internal bool TryGetSecondsSinceSuccess(out double seconds)
    {
        seconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSuccessTimestamp)).TotalSeconds;
        return _hasStarted || Options.Schedule is not null;
    }

    // Reported for workloads that expect heartbeats or have sent one.
    internal bool TryGetSecondsSinceHeartbeat(out double seconds)
    {
        seconds = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastHeartbeatTimestamp)).TotalSeconds;
        return _hasBeaten || Options.ExpectEvery is not null;
    }

    internal void Complete(long startTimestamp, Activity? activity, Outcome outcome, string? errorType, Exception? exception, string? description)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(startTimestamp, now).TotalSeconds;
        var outcomeTag = outcome switch
        {
            Outcome.Success => s_success,
            Outcome.Failure => s_failure,
            _ => s_cancelled,
        };

        if (errorType is null)
            _instruments.Runs.Add(1, _nameTag, outcomeTag);
        else
            _instruments.Runs.Add(1, _nameTag, outcomeTag, new(RunSignalTelemetry.Attributes.ErrorType, errorType));
        _instruments.Duration.Record(seconds, _nameTag, outcomeTag);

        if (outcome == Outcome.Success)
            Volatile.Write(ref _lastSuccessTimestamp, now);

        if (activity is not null)
            EndActivity(activity, outcome, errorType, exception, description);
    }

    private static void EndActivity(Activity activity, Outcome outcome, string? errorType, Exception? exception, string? description)
    {
        activity.SetTag(RunSignalTelemetry.Attributes.Outcome, outcome switch
        {
            Outcome.Success => "success",
            Outcome.Failure => "failure",
            _ => "cancelled",
        });

        if (outcome == Outcome.Failure)
        {
            activity.SetTag(RunSignalTelemetry.Attributes.ErrorType, errorType);
            activity.SetStatus(ActivityStatusCode.Error, description);
            if (exception is not null)
            {
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    ["exception.type"] = errorType,
                    ["exception.message"] = exception.Message,
                    ["exception.stacktrace"] = exception.ToString(),
                }));
            }
        }
        else if (outcome == Outcome.Success)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        activity.Dispose();
    }
}
