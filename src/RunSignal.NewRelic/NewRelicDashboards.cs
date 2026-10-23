using System.Text.Json;
using System.Text.Json.Nodes;
using static RunSignal.RunSignalTelemetry;

namespace RunSignal.NewRelic;

/// <summary>One widget of a RunSignal dashboard.</summary>
/// <param name="Title">The widget title.</param>
/// <param name="Visualization">The New Relic visualization, such as <c>viz.line</c>.</param>
/// <param name="Nrql">The NRQL query.</param>
/// <param name="Column">The 1-based column (of 12).</param>
/// <param name="Row">The 1-based row.</param>
/// <param name="Width">The width in columns.</param>
/// <param name="Height">The height in rows.</param>
public sealed record DashboardWidget(string Title, string Visualization, string Nrql, int Column, int Row, int Width, int Height);

/// <summary>
/// The New Relic dashboard RunSignal manages for a service: one page with the health of every workload, built
/// from the same signals the alerts use. It is the place to look when an alert fires.
/// </summary>
public sealed class NewRelicDashboards
{
    private readonly NerdGraphClient _client;
    private readonly long _accountId;

    /// <summary>Creates a dashboard manager for one New Relic account.</summary>
    public NewRelicDashboards(NerdGraphClient client, long accountId)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _accountId = accountId;
    }

    /// <summary>The dashboard name for a service, such as <c>RunSignal: billing</c>.</summary>
    public static string Name(string service) => $"RunSignal: {service}";

    /// <summary>The widgets of the dashboard for <paramref name="service"/>. Pure: it calls no API.</summary>
    public static IReadOnlyList<DashboardWidget> Widgets(string service)
    {
        var from = $"FROM Metric WHERE service.name = {Quote(service)}";
        var failure = $"{Attributes.Outcome} = 'failure'";
        return
        [
            new("Runs in the last hour", "viz.billboard",
                $"SELECT filter(sum({Instruments.Runs}), WHERE {Attributes.Outcome} = 'success') AS 'Succeeded', filter(sum({Instruments.Runs}), WHERE {failure}) AS 'Failed', filter(sum({Instruments.Runs}), WHERE {Attributes.Outcome} = 'cancelled') AS 'Cancelled' {from} SINCE 1 hour ago",
                1, 1, 4, 3),
            new("Workloads", "viz.table",
                $"SELECT latest({Instruments.SinceSuccess}) AS 'Seconds since success', latest({Instruments.SinceHeartbeat}) AS 'Seconds since heartbeat', sum({Instruments.Runs}) AS 'Runs', filter(sum({Instruments.Runs}), WHERE {failure}) AS 'Failed runs' {from} FACET {Attributes.Workload} SINCE 1 day ago LIMIT MAX",
                5, 1, 8, 3),
            new("Runs by outcome", "viz.line",
                $"SELECT sum({Instruments.Runs}) {from} FACET {Attributes.Workload}, {Attributes.Outcome} SINCE 1 day ago TIMESERIES",
                1, 4, 6, 3),
            new("Run duration (p95, seconds)", "viz.line",
                $"SELECT percentile({Instruments.Duration}, 95) {from} FACET {Attributes.Workload} SINCE 1 day ago TIMESERIES",
                7, 4, 6, 3),
            new("Seconds since last success", "viz.line",
                $"SELECT max({Instruments.SinceSuccess}) {from} FACET {Attributes.Workload} SINCE 1 day ago TIMESERIES",
                1, 7, 6, 3),
            new("Items processed", "viz.line",
                $"SELECT filter(sum({Instruments.Items}), WHERE {Attributes.ItemResult} = 'processed') {from} FACET {Attributes.Workload} SINCE 1 day ago TIMESERIES",
                7, 7, 6, 3),
            new("Failures by error type", "viz.table",
                $"SELECT sum({Instruments.Runs}) AS 'Failed runs' {from} AND {failure} FACET {Attributes.Workload}, {Attributes.ErrorType} SINCE 1 day ago LIMIT MAX",
                1, 10, 12, 3),
        ];
    }

    /// <summary>How long to wait for a new dashboard to become searchable. New Relic's entity search lags behind writes.</summary>
    public TimeSpan SearchableTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Creates or updates the dashboard for <paramref name="service"/>; a dry run only reports the changes. Duplicates
    /// that RunSignal created, for example by two runs before New Relic's search caught up, are deleted.
    /// </summary>
    public async Task<IReadOnlyList<Change>> ProvisionAsync(string service, bool dryRun, CancellationToken cancellationToken = default)
    {
        var name = Name(service);
        var found = await FindAllAsync(name, cancellationToken).ConfigureAwait(false);
        var changes = new List<Change>();
        foreach (var duplicate in found.Skip(1))
        {
            if (!await IsManagedAsync(duplicate, cancellationToken).ConfigureAwait(false))
                continue;
            changes.Add(new Change(ChangeKind.Delete, "dashboard", $"{name} (duplicate)"));
            if (!dryRun)
                await DeleteAsync(duplicate, cancellationToken).ConfigureAwait(false);
        }

        var existing = found.FirstOrDefault();
        var input = Input(service);
        if (existing is null)
        {
            if (!dryRun)
            {
                const string create = """
                    mutation($accountId: Int!, $dashboard: DashboardInput!) {
                      dashboardCreate(accountId: $accountId, dashboard: $dashboard) {
                        entityResult { guid }
                        errors { description type }
                      }
                    }
                    """;
                await ExecuteAsync(create, "dashboardCreate", new JsonObject { ["accountId"] = _accountId, ["dashboard"] = input }, cancellationToken).ConfigureAwait(false);
                await WaitUntilSearchableAsync(name, cancellationToken).ConfigureAwait(false);
            }
            changes.Add(new Change(ChangeKind.Create, "dashboard", name));
            return changes;
        }

        if (await MatchesAsync(existing, service, cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new Change(ChangeKind.Unchanged, "dashboard", name));
            return changes;
        }

        if (!dryRun)
        {
            const string update = """
                mutation($guid: EntityGuid!, $dashboard: DashboardInput!) {
                  dashboardUpdate(guid: $guid, dashboard: $dashboard) {
                    entityResult { guid }
                    errors { description type }
                  }
                }
                """;
            await ExecuteAsync(update, "dashboardUpdate", new JsonObject { ["guid"] = existing, ["dashboard"] = input }, cancellationToken).ConfigureAwait(false);
        }
        changes.Add(new Change(ChangeKind.Update, "dashboard", name));
        return changes;
    }

    /// <summary>Deletes the dashboards for <paramref name="service"/> that RunSignal created.</summary>
    public async Task<IReadOnlyList<Change>> DestroyAsync(string service, bool dryRun, CancellationToken cancellationToken = default)
    {
        var name = Name(service);
        var changes = new List<Change>();
        foreach (var guid in await FindAllAsync(name, cancellationToken).ConfigureAwait(false))
        {
            if (!await IsManagedAsync(guid, cancellationToken).ConfigureAwait(false))
                continue;
            changes.Add(new Change(ChangeKind.Delete, "dashboard", name));
            if (!dryRun)
                await DeleteAsync(guid, cancellationToken).ConfigureAwait(false);
        }
        return changes;
    }

    private Task DeleteAsync(string guid, CancellationToken cancellationToken)
    {
        const string delete = """
            mutation($guid: EntityGuid!) {
              dashboardDelete(guid: $guid) { status errors { description type } }
            }
            """;
        return ExecuteAsync(delete, "dashboardDelete", new JsonObject { ["guid"] = guid }, cancellationToken);
    }

    private async Task WaitUntilSearchableAsync(string name, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SearchableTimeout;
        var delay = TimeSpan.FromMilliseconds(500);
        while (DateTime.UtcNow < deadline)
        {
            if ((await FindAllAsync(name, cancellationToken).ConfigureAwait(false)).Count > 0)
                return;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
        }
    }

    private async Task<bool> IsManagedAsync(string guid, CancellationToken cancellationToken)
    {
        const string query = """
            query($guid: EntityGuid!) {
              actor { entity(guid: $guid) { ... on DashboardEntity { description } } }
            }
            """;
        var data = await _client.ExecuteAsync(query, new JsonObject { ["guid"] = guid }, cancellationToken).ConfigureAwait(false);
        // A dashboard deleted moments ago can still be in the search index, with no entity behind it.
        var entity = data.GetProperty("actor").GetProperty("entity");
        return entity.ValueKind == JsonValueKind.Object &&
               entity.TryGetProperty("description", out var d) &&
               d.GetString()?.StartsWith(AlertPlanner.ManagedMarker, StringComparison.Ordinal) == true;
    }

    private JsonObject Input(string service) => new()
    {
        ["name"] = Name(service),
        ["description"] = $"{AlertPlanner.ManagedMarker} Workload health for {service}. Regenerated by `runsignal provision`; edits are overwritten.",
        ["permissions"] = "PUBLIC_READ_ONLY",
        ["pages"] = new JsonArray(new JsonObject
        {
            ["name"] = "Workloads",
            ["widgets"] = new JsonArray(Widgets(service).Select(w => (JsonNode)new JsonObject
            {
                ["title"] = w.Title,
                ["layout"] = new JsonObject { ["column"] = w.Column, ["row"] = w.Row, ["width"] = w.Width, ["height"] = w.Height },
                ["visualization"] = new JsonObject { ["id"] = w.Visualization },
                ["rawConfiguration"] = new JsonObject
                {
                    ["nrqlQueries"] = new JsonArray(new JsonObject { ["accountIds"] = new JsonArray(_accountId), ["query"] = w.Nrql }),
                },
            }).ToArray()),
        }),
    };

    // The guids of the dashboards called name in this account (not their pages), oldest first.
    private async Task<IReadOnlyList<string>> FindAllAsync(string name, CancellationToken cancellationToken)
    {
        const string query = """
            query($query: String!) {
              actor { entitySearch(query: $query) { results { entities { guid name ... on DashboardEntityOutline { dashboardParentGuid } } } } }
            }
            """;
        var escaped = name.Replace("\\", "\\\\").Replace("'", "\\'");
        var data = await _client.ExecuteAsync(query, new JsonObject
        {
            ["query"] = $"type = 'DASHBOARD' AND accountId = {_accountId} AND name = '{escaped}'",
        }, cancellationToken).ConfigureAwait(false);

        var guids = new List<string>();
        foreach (var entity in data.GetProperty("actor").GetProperty("entitySearch").GetProperty("results").GetProperty("entities").EnumerateArray())
        {
            // Each page is also an entity; only the dashboard itself has no parent.
            var isPage = entity.TryGetProperty("dashboardParentGuid", out var parent) && parent.ValueKind == JsonValueKind.String;
            if (entity.GetProperty("name").GetString() == name && !isPage)
                guids.Add(entity.GetProperty("guid").GetString()!);
        }
        // Guids end in the dashboard's numeric ID, so ordering keeps the first one created.
        return guids.OrderBy(Base64Id).ToArray();
    }

    private static long Base64Id(string guid)
    {
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(guid.PadRight(guid.Length + (4 - guid.Length % 4) % 4, '=')));
            return long.TryParse(decoded[(decoded.LastIndexOf(':') + 1)..], out var id) ? id : long.MaxValue;
        }
        catch (FormatException)
        {
            return long.MaxValue;
        }
    }

    // Compares what RunSignal sets, so the dashboard is only updated when the widgets changed.
    private async Task<bool> MatchesAsync(string guid, string service, CancellationToken cancellationToken)
    {
        const string query = """
            query($guid: EntityGuid!) {
              actor { entity(guid: $guid) { ... on DashboardEntity {
                description
                pages { widgets { title visualization { id } layout { column row width height } rawConfiguration } }
              } } }
            }
            """;
        var data = await _client.ExecuteAsync(query, new JsonObject { ["guid"] = guid }, cancellationToken).ConfigureAwait(false);
        var entity = data.GetProperty("actor").GetProperty("entity");
        if (entity.ValueKind != JsonValueKind.Object)
            return false;
        if (entity.GetProperty("description").GetString() is not { } description || !description.StartsWith(AlertPlanner.ManagedMarker, StringComparison.Ordinal))
            return true; // not RunSignal's: leave it alone

        var pages = entity.GetProperty("pages");
        if (pages.GetArrayLength() != 1)
            return false;
        var current = pages[0].GetProperty("widgets").EnumerateArray().ToArray();
        var wanted = Widgets(service);
        if (current.Length != wanted.Count)
            return false;

        for (var i = 0; i < wanted.Count; i++)
        {
            var (c, w) = (current[i], wanted[i]);
            var layout = c.GetProperty("layout");
            var queries = c.GetProperty("rawConfiguration").GetProperty("nrqlQueries");
            if (c.GetProperty("title").GetString() != w.Title ||
                c.GetProperty("visualization").GetProperty("id").GetString() != w.Visualization ||
                layout.GetProperty("column").GetInt32() != w.Column || layout.GetProperty("row").GetInt32() != w.Row ||
                layout.GetProperty("width").GetInt32() != w.Width || layout.GetProperty("height").GetInt32() != w.Height ||
                queries.GetArrayLength() != 1 || queries[0].GetProperty("query").GetString() != w.Nrql)
                return false;
        }
        return true;
    }

    private async Task ExecuteAsync(string mutation, string field, JsonObject variables, CancellationToken cancellationToken)
    {
        var data = await _client.ExecuteAsync(mutation, variables, cancellationToken).ConfigureAwait(false);
        if (data.GetProperty(field).TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new NerdGraphException($"{field} failed: " + string.Join("; ", errors.EnumerateArray().Select(e => e.GetProperty("description").GetString())));
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
