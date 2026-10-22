using System.Globalization;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.NewRelic;

/// <summary>Turns a workload manifest into New Relic alert policies and NRQL conditions. Pure: it calls no API.</summary>
public static class AlertPlanner
{
    /// <summary>Every description RunSignal writes starts with this, so it only ever changes conditions it created.</summary>
    public const string ManagedMarker = "Managed by RunSignal.";

    // New Relic limits for NRQL conditions.
    private const int MaxLossOfSignalSeconds = 172_800;
    private const int MinLossOfSignalSeconds = 30;

    /// <summary>Plans the alerts for one service's manifest.</summary>
    public static AlertPlan Plan(WorkloadManifest manifest, AlertPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new AlertPlanOptions();
        var warnings = new List<string>();
        var bySeverity = Enum.GetValues<Severity>().ToDictionary(s => s, _ => new List<AlertCondition>());
        var notReporting = Math.Clamp((int)options.NotReportingAfter.TotalSeconds, MinLossOfSignalSeconds, MaxLossOfSignalSeconds);

        foreach (var workload in manifest.Workloads)
        {
            var conditions = bySeverity[workload.Severity];
            var where = $"service.name = {Quote(manifest.Service)} AND {Attributes.Workload} = {Quote(workload.Name)}";
            var before = conditions.Count;

            if (workload.Schedule is { } schedule)
            {
                try
                {
                    var interval = CronSchedule.MaxInterval(schedule, options.ScheduleTimeZone, options.Now);
                    var grace = workload.MaxDurationSeconds is { } max ? TimeSpan.FromSeconds(max) : options.MissedGrace;
                    var limit = interval + grace;
                    conditions.Add(new AlertCondition
                    {
                        Name = $"{workload.Name}: missed",
                        Workload = workload.Name,
                        Kind = AlertKind.Missed,
                        Description = $"{ManagedMarker} {workload.Name} has not succeeded for longer than its schedule ({schedule}) allows: {Format(interval)} between runs plus {Format(grace)} grace. Also opens when the service stops reporting for {Format(TimeSpan.FromSeconds(notReporting))}.",
                        Nrql = $"SELECT max({Instruments.SinceSuccess}) FROM Metric WHERE {where}",
                        Above = true,
                        Threshold = Math.Round(limit.TotalSeconds),
                        AggregationWindowSeconds = 60,
                        AggregationMethod = AggregationMethod.EventFlow,
                        LossOfSignalSeconds = notReporting,
                    });

                    if (workload.FailureRateThreshold is null)
                    {
                        conditions.Add(new AlertCondition
                        {
                            Name = $"{workload.Name}: failed",
                            Workload = workload.Name,
                            Kind = AlertKind.Failed,
                            Description = $"{ManagedMarker} A run of {workload.Name} failed. Set a failure-rate threshold to alert on a share of failures instead.",
                            Nrql = $"SELECT filter(sum({Instruments.Runs}), WHERE {Attributes.Outcome} = 'failure') FROM Metric WHERE {where}",
                            Above = true,
                            Threshold = 0,
                            AggregationWindowSeconds = 60,
                            AggregationMethod = AggregationMethod.EventTimer,
                            AggregationTimerSeconds = 60,
                        });
                    }
                }
                catch (FormatException ex)
                {
                    warnings.Add($"{workload.Name}: no missed-run alert, because the schedule could not be read. {ex.Message}");
                }
            }

            if (workload.ExpectEverySeconds is { } expectEvery)
            {
                conditions.Add(new AlertCondition
                {
                    Name = $"{workload.Name}: stalled",
                    Workload = workload.Name,
                    Kind = AlertKind.Stalled,
                    Description = $"{ManagedMarker} {workload.Name} has not sent a heartbeat for longer than {Format(TimeSpan.FromSeconds(expectEvery))}. Also opens when the service stops reporting for {Format(TimeSpan.FromSeconds(notReporting))}.",
                    Nrql = $"SELECT max({Instruments.SinceHeartbeat}) FROM Metric WHERE {where}",
                    Above = true,
                    Threshold = Math.Round(expectEvery),
                    AggregationWindowSeconds = 60,
                    AggregationMethod = AggregationMethod.EventFlow,
                    LossOfSignalSeconds = notReporting,
                });
            }

            if (workload.MaxDurationSeconds is { } maxDuration)
            {
                conditions.Add(new AlertCondition
                {
                    Name = $"{workload.Name}: overrun",
                    Workload = workload.Name,
                    Kind = AlertKind.Overrun,
                    Description = $"{ManagedMarker} A run of {workload.Name} took longer than {Format(TimeSpan.FromSeconds(maxDuration))}. A run that never finishes shows up as missed or stalled instead.",
                    Nrql = $"SELECT max({Instruments.Duration}) FROM Metric WHERE {where}",
                    Above = true,
                    Threshold = maxDuration,
                    AggregationWindowSeconds = 60,
                    AggregationMethod = AggregationMethod.EventTimer,
                    AggregationTimerSeconds = 60,
                });
            }

            if (workload.MinProcessed is { } minProcessed)
            {
                conditions.Add(new AlertCondition
                {
                    Name = $"{workload.Name}: low output",
                    Workload = workload.Name,
                    Kind = AlertKind.LowOutput,
                    Description = $"{ManagedMarker} Successful runs of {workload.Name} processed fewer than {minProcessed} items on average within an hour. Record 0 with Processed(0) so empty runs count.",
                    Nrql = $"SELECT filter(sum({Instruments.Items}), WHERE {Attributes.ItemResult} = 'processed') / filter(sum({Instruments.Runs}), WHERE {Attributes.Outcome} = 'success') FROM Metric WHERE {where}",
                    Above = false,
                    Threshold = minProcessed,
                    AggregationWindowSeconds = 3600,
                    AggregationMethod = AggregationMethod.EventTimer,
                    AggregationTimerSeconds = 600,
                });
            }

            if (workload.FailureRateThreshold is { } rate)
            {
                var percent = rate.ToString("P0", CultureInfo.InvariantCulture);
                conditions.Add(new AlertCondition
                {
                    Name = $"{workload.Name}: failure rate",
                    Workload = workload.Name,
                    Kind = AlertKind.FailureRate,
                    Description = $"{ManagedMarker} More than {percent} of the runs of {workload.Name} failed within 15 minutes. Cancelled runs are not counted.",
                    Nrql = $"SELECT filter(sum({Instruments.Runs}), WHERE {Attributes.Outcome} = 'failure') / filter(sum({Instruments.Runs}), WHERE {Attributes.Outcome} IN ('success', 'failure')) FROM Metric WHERE {where}",
                    Above = true,
                    Threshold = rate,
                    AggregationWindowSeconds = 900,
                    AggregationMethod = AggregationMethod.EventTimer,
                    AggregationTimerSeconds = 300,
                });
                conditions.Add(new AlertCondition
                {
                    Name = $"{workload.Name}: item failure rate",
                    Workload = workload.Name,
                    Kind = AlertKind.ItemFailureRate,
                    Description = $"{ManagedMarker} More than {percent} of the items {workload.Name} handled failed within 15 minutes.",
                    Nrql = $"SELECT filter(sum({Instruments.Items}), WHERE {Attributes.ItemResult} = 'failed') / sum({Instruments.Items}) FROM Metric WHERE {where}",
                    Above = true,
                    Threshold = rate,
                    AggregationWindowSeconds = 900,
                    AggregationMethod = AggregationMethod.EventTimer,
                    AggregationTimerSeconds = 300,
                });
            }

            if (conditions.Count == before && !warnings.Any(w => w.StartsWith(workload.Name + ":", StringComparison.Ordinal)))
                warnings.Add($"{workload.Name}: no alerts, because it has no expectations. Set a schedule, expectEvery, maxDuration, minProcessed or failureRateThreshold.");
        }

        var policies = bySeverity
            .Select(pair => new AlertPolicyPlan(PolicyName(manifest.Service, pair.Key), pair.Key, pair.Value))
            .ToArray();
        return new AlertPlan(manifest.Service, policies, warnings);
    }

    /// <summary>The name of the policy RunSignal manages for a service and severity, such as <c>RunSignal: billing (critical)</c>.</summary>
    public static string PolicyName(string service, Severity severity) =>
        $"RunSignal: {service} ({severity.ToString().ToLowerInvariant()})";

    // NRQL string literal. Workload names cannot contain quotes, but service names can.
    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string Format(TimeSpan span) =>
        span.TotalDays >= 1 && span.TotalHours % 24 == 0 ? $"{span.TotalDays:0.#}d"
        : span.TotalHours >= 1 ? $"{span.TotalHours:0.#}h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#}m"
        : $"{span.TotalSeconds:0.#}s";
}
