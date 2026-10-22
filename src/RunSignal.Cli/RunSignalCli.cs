using System.CommandLine;
using System.Globalization;
using RunSignal.NewRelic;

namespace RunSignal.Cli;

/// <summary>The <c>runsignal</c> command line: plan, diff and provision alerts from workload manifests.</summary>
internal static class RunSignalCli
{
    private const string ApiKeyVariable = "NEW_RELIC_API_KEY";

    /// <param name="args">Command line arguments.</param>
    /// <param name="output">Where results go.</param>
    /// <param name="error">Where errors go.</param>
    /// <param name="handler">The HTTP handler for NerdGraph; tests pass a fake.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpMessageHandler? handler = null)
    {
        var manifests = new Argument<FileInfo[]>("manifests")
        {
            Description = "One or more workload manifests written by the RunSignal SDK (RUNSIGNAL_MANIFEST_PATH).",
            Arity = ArgumentArity.OneOrMore,
        };
        var grace = new Option<double>("--missed-grace")
        {
            Description = "Minutes a scheduled workload may run late before it counts as missed, when it has no max duration.",
            DefaultValueFactory = _ => 15,
        };
        var notReporting = new Option<double>("--not-reporting-after")
        {
            Description = "Minutes without any signal before an incident opens because the service stopped reporting.",
            DefaultValueFactory = _ => 15,
        };
        var timeZone = new Option<string>("--time-zone")
        {
            Description = "Time zone the cron schedules run in, such as Europe/Istanbul.",
            DefaultValueFactory = _ => "UTC",
        };
        var accountId = new Option<long>("--account-id")
        {
            Description = "The New Relic account ID.",
            Required = true,
        };
        var region = new Option<NewRelicRegion>("--region")
        {
            Description = "The New Relic data center: US, EU or JP.",
            DefaultValueFactory = _ => NewRelicRegion.US,
        };
        var apiKey = new Option<string?>("--api-key")
        {
            Description = $"A New Relic User API key. Prefer the {ApiKeyVariable} environment variable.",
        };
        var channels = new Option<string[]>("--channel")
        {
            Description = "Route a severity to a New Relic notification channel: --channel critical=<channel-id>. Repeatable. " +
                          "Create the Teams or Slack destination and channel in New Relic first.",
        };

        var plan = new Command("plan", "Show the alerts the manifests produce, without contacting New Relic.") { manifests, grace, notReporting, timeZone };
        plan.SetAction(Guarded(async (result, ct) =>
        {
            foreach (var alertPlan in await LoadPlansAsync(result, manifests, grace, notReporting, timeZone, ct))
                WritePlan(output, alertPlan);
            return 0;
        }));

        var diff = new Command("diff", "Show what provision would change in New Relic, without changing anything.")
        {
            manifests, accountId, region, apiKey, channels, grace, notReporting, timeZone,
        };
        diff.SetAction(Guarded((result, ct) => ProvisionAsync(result, dryRun: true, ct)));

        var provision = new Command("provision", "Create, update and delete the New Relic policies, conditions and workflows RunSignal manages.")
        {
            manifests, accountId, region, apiKey, channels, grace, notReporting, timeZone,
        };
        provision.SetAction(Guarded((result, ct) => ProvisionAsync(result, dryRun: false, ct)));

        var root = new RootCommand("RunSignal: alerts for background work that no HTTP status code describes.") { plan, diff, provision };
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

        // Expected failures (a missing file, a bad cron, an API error) print one line instead of a stack trace.
        Func<ParseResult, CancellationToken, Task<int>> Guarded(Func<ParseResult, CancellationToken, Task<int>> action) => async (result, ct) =>
        {
            try
            {
                return await action(result, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NerdGraphException or FormatException or IOException or ArgumentException
                                       or System.Text.Json.JsonException or HttpRequestException or TimeZoneNotFoundException)
            {
                await error.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
                return 1;
            }
        };

        async Task<int> ProvisionAsync(ParseResult result, bool dryRun, CancellationToken ct)
        {
            var key = result.GetValue(apiKey) ?? Environment.GetEnvironmentVariable(ApiKeyVariable);
            if (string.IsNullOrWhiteSpace(key))
            {
                await error.WriteLineAsync($"error: pass --api-key or set {ApiKeyVariable} to a New Relic User API key.").ConfigureAwait(false);
                return 1;
            }

            var routes = ParseChannels(result.GetValue(channels) ?? []);
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            var provisioner = new NewRelicProvisioner(new NerdGraphClient(http, key, result.GetValue(region)), result.GetValue(accountId));

            var changed = 0;
            foreach (var alertPlan in await LoadPlansAsync(result, manifests, grace, notReporting, timeZone, ct))
            {
                WriteWarnings(output, alertPlan);
                var changes = await provisioner.ProvisionAsync(alertPlan, routes, dryRun, ct).ConfigureAwait(false);
                await output.WriteLineAsync($"{alertPlan.Service}:").ConfigureAwait(false);
                foreach (var change in changes)
                    await output.WriteLineAsync($"  {Symbol(change.Kind)} {change.Resource} {change.Name}").ConfigureAwait(false);
                changed += changes.Count(c => c.Kind != ChangeKind.Unchanged);
            }

            var verb = dryRun ? "to change" : "changed";
            await output.WriteLineAsync(changed == 0 ? "No changes." : $"{changed} {(changed == 1 ? "resource" : "resources")} {verb}.").ConfigureAwait(false);
            return 0;
        }
    }

    private static async Task<List<AlertPlan>> LoadPlansAsync(
        ParseResult result, Argument<FileInfo[]> manifests, Option<double> grace, Option<double> notReporting, Option<string> timeZone, CancellationToken ct)
    {
        var options = new AlertPlanOptions
        {
            MissedGrace = TimeSpan.FromMinutes(result.GetValue(grace)),
            NotReportingAfter = TimeSpan.FromMinutes(result.GetValue(notReporting)),
            ScheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(result.GetValue(timeZone)!),
        };

        var plans = new List<AlertPlan>();
        foreach (var file in result.GetValue(manifests)!)
        {
            var manifest = WorkloadManifest.FromJson(await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false));
            plans.Add(AlertPlanner.Plan(manifest, options));
        }
        return plans;
    }

    internal static Dictionary<Severity, IReadOnlyList<string>> ParseChannels(IEnumerable<string> values)
    {
        var routes = new Dictionary<Severity, List<string>>();
        foreach (var value in values)
        {
            var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[1].Length == 0 || !Enum.TryParse<Severity>(parts[0], ignoreCase: true, out var severity))
                throw new ArgumentException($"--channel '{value}' must look like critical=<channel-id> (severity: info, warning or critical).");
            if (!routes.TryGetValue(severity, out var ids))
                routes[severity] = ids = [];
            ids.Add(parts[1]);
        }
        return routes.ToDictionary(r => r.Key, r => (IReadOnlyList<string>)r.Value);
    }

    private static void WritePlan(TextWriter output, AlertPlan plan)
    {
        output.WriteLine($"{plan.Service}:");
        foreach (var policy in plan.Policies.Where(p => p.Conditions.Count > 0))
        {
            output.WriteLine($"  policy {policy.Name}");
            foreach (var condition in policy.Conditions)
            {
                var comparison = condition.Above ? ">" : "<";
                output.WriteLine($"    {condition.Name}  ({condition.Kind}, {comparison} {condition.Threshold.ToString(CultureInfo.InvariantCulture)})");
                output.WriteLine($"      {condition.Nrql}");
            }
        }
        WriteWarnings(output, plan);
    }

    private static void WriteWarnings(TextWriter output, AlertPlan plan)
    {
        foreach (var warning in plan.Warnings)
            output.WriteLine($"  warning: {warning}");
    }

    private static char Symbol(ChangeKind kind) => kind switch
    {
        ChangeKind.Create => '+',
        ChangeKind.Update => '~',
        ChangeKind.Delete => '-',
        _ => '=',
    };
}
