using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace RunSignal.Tests;

// spec/conformance/signals.json: every SDK runs the same scenario and must emit exactly the same metrics.
public class ConformanceTests
{
    [Fact]
    public void Scenario_produces_exactly_the_expected_metrics()
    {
        var spec = Conformance.Load("signals.json");
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddRunSignalInstrumentation()
            .AddInMemoryExporter(exported)
            .Build();
        using var registry = new WorkloadRegistry();

        foreach (var w in spec.GetProperty("workloads").EnumerateArray())
        {
            registry.Get(w.GetProperty("name").GetString()!, o =>
            {
                if (w.TryGetProperty("schedule", out var schedule)) o.Schedule = schedule.GetString();
                if (w.TryGetProperty("maxDurationSeconds", out var max)) o.MaxDuration = TimeSpan.FromSeconds(max.GetDouble());
                if (w.TryGetProperty("expectEverySeconds", out var every)) o.ExpectEvery = TimeSpan.FromSeconds(every.GetDouble());
                if (w.TryGetProperty("minProcessed", out var min)) o.MinProcessed = min.GetInt64();
                if (w.TryGetProperty("failureRateThreshold", out var rate)) o.FailureRateThreshold = rate.GetDouble();
                if (w.TryGetProperty("severity", out var severity)) o.Severity = Enum.Parse<Severity>(severity.GetString()!, ignoreCase: true);
            });
        }

        foreach (var a in spec.GetProperty("actions").EnumerateArray())
        {
            var workload = registry.Get(a.GetProperty("workload").GetString()!);
            long Count() => a.GetProperty("count").GetInt64();
            switch (a.GetProperty("action").GetString())
            {
                case "succeed": workload.Start().Succeed(); break;
                case "failCode": workload.Start().Fail(a.GetProperty("code").GetString()!); break;
                case "cancel": workload.Start().Cancel(); break;
                case "processed": workload.Processed(Count()); break;
                case "itemFailed": workload.ItemFailed(Count()); break;
                case "beat": workload.Beat(); break;
                default: throw new InvalidOperationException("Unknown action " + a.GetProperty("action"));
            }
        }

        provider.ForceFlush();
        Assert.Equal(Canonical(spec.GetProperty("expected")), Canonical(Normalize(exported)));
    }

    // Other tests record on meters named RunSignal at the same time, so only the scenario's workloads count.
    private static JsonArray Normalize(IEnumerable<Metric> metrics)
    {
        var result = new JsonArray();
        foreach (var metric in metrics)
        {
            var type = metric.MetricType switch
            {
                MetricType.LongSum => "counter",
                MetricType.Histogram => "histogram",
                MetricType.DoubleGauge => "gauge",
                var other => other.ToString(),
            };
            var points = new JsonArray();
            JsonArray? bounds = null;
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var attributes = new JsonObject();
                foreach (var tag in point.Tags)
                    attributes[tag.Key] = tag.Value?.ToString();
                if (attributes["runsignal.workload"]?.GetValue<string>() is not { } name || !name.StartsWith("conf-", StringComparison.Ordinal))
                    continue;

                var entry = new JsonObject { ["attributes"] = attributes };
                if (type == "counter")
                    entry["value"] = point.GetSumLong();
                else if (type == "histogram")
                {
                    entry["count"] = point.GetHistogramCount();
                    bounds = new JsonArray();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                            bounds.Add(bucket.ExplicitBound);
                    }
                }
                points.Add(entry);
            }
            if (points.Count == 0)
                continue;

            var normalized = new JsonObject { ["name"] = metric.Name, ["type"] = type, ["unit"] = metric.Unit };
            if (bounds is not null)
                normalized["bounds"] = bounds;
            normalized["points"] = points;
            result.Add(normalized);
        }
        return result;
    }

    // Order-insensitive form: metrics sorted by name, points by their attributes, numbers as plain numbers.
    private static string Canonical(JsonElement element) => Canonical(JsonNode.Parse(element.GetRawText())!.AsArray());

    // 3, 3L and 3.0 are the same number here, whichever type produced them.
    private static double Number(JsonNode node) => double.Parse(node.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    private static string Canonical(JsonArray metrics)
    {
        static string Key(JsonNode? node) => node!.ToJsonString();
        var sorted = metrics
            .Select(m =>
            {
                var o = m!.AsObject();
                var copy = new JsonObject();
                foreach (var key in o.Select(p => p.Key).Order(StringComparer.Ordinal))
                {
                    if (key == "points")
                    {
                        var points = o["points"]!.AsArray().Select(p =>
                        {
                            var po = p!.AsObject();
                            var attrs = new JsonObject();
                            foreach (var (k, v) in po["attributes"]!.AsObject().OrderBy(x => x.Key, StringComparer.Ordinal))
                                attrs[k] = v!.DeepClone();
                            var pc = new JsonObject { ["attributes"] = attrs };
                            if (po["value"] is { } value) pc["value"] = Number(value);
                            if (po["count"] is { } count) pc["count"] = Number(count);
                            return pc;
                        }).OrderBy(Key, StringComparer.Ordinal).Select(p => (JsonNode)p).ToArray();
                        copy["points"] = new JsonArray(points);
                    }
                    else if (key == "bounds")
                        copy["bounds"] = new JsonArray(o["bounds"]!.AsArray().Select(b => (JsonNode)Number(b!)).ToArray());
                    else
                        copy[key] = o[key]!.DeepClone();
                }
                return copy;
            })
            .OrderBy(m => m["name"]!.GetValue<string>(), StringComparer.Ordinal);
        return new JsonArray(sorted.Select(m => (JsonNode)m).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
