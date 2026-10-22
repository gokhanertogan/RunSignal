using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunSignal.NewRelic;

/// <summary>What provisioning did, or would do in a dry run, to one resource.</summary>
public enum ChangeKind
{
    /// <summary>The resource is created.</summary>
    Create,

    /// <summary>The resource exists and is changed to match the plan.</summary>
    Update,

    /// <summary>The resource was created by RunSignal and is no longer in the plan.</summary>
    Delete,

    /// <summary>The resource already matches the plan.</summary>
    Unchanged,
}

/// <summary>One change to a New Relic resource.</summary>
/// <param name="Kind">What happens to the resource.</param>
/// <param name="Resource"><c>policy</c>, <c>condition</c> or <c>workflow</c>.</param>
/// <param name="Name">The resource name.</param>
public sealed record Change(ChangeKind Kind, string Resource, string Name);

/// <summary>
/// Makes a New Relic account match an <see cref="AlertPlan"/>: creates, updates and deletes the policies, NRQL
/// conditions and workflows RunSignal manages. It never touches conditions it did not create, and running it
/// again without changes changes nothing.
/// </summary>
public sealed class NewRelicProvisioner
{
    private const int ViolationTimeLimitSeconds = 259_200;

    // Seconds New Relic waits for late data before closing an EVENT_FLOW window; required for that method.
    private const int AggregationDelaySeconds = 120;

    private readonly NerdGraphClient _client;
    private readonly long _accountId;

    /// <summary>Creates a provisioner for one New Relic account.</summary>
    public NewRelicProvisioner(NerdGraphClient client, long accountId)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _accountId = accountId;
    }

    /// <summary>Applies <paramref name="plan"/>, or with <paramref name="dryRun"/> only reports what would change.</summary>
    /// <param name="plan">The alerts to provision.</param>
    /// <param name="channels">Notification channel IDs per severity. A workflow is managed only for severities that have channels.</param>
    /// <param name="dryRun"><c>true</c> to read the account and report changes without making them.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<IReadOnlyList<Change>> ProvisionAsync(
        AlertPlan plan,
        IReadOnlyDictionary<Severity, IReadOnlyList<string>> channels,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(channels);
        var changes = new List<Change>();

        foreach (var policy in plan.Policies)
        {
            var policyId = await FindPolicyIdAsync(policy.Name, cancellationToken).ConfigureAwait(false);
            if (policyId is null)
            {
                if (policy.Conditions.Count == 0)
                    continue;
                changes.Add(new Change(ChangeKind.Create, "policy", policy.Name));
                if (!dryRun)
                    policyId = await CreatePolicyAsync(policy.Name, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                changes.Add(new Change(ChangeKind.Unchanged, "policy", policy.Name));
            }

            var existing = policyId is null
                ? []
                : await ListConditionsAsync(policyId, cancellationToken).ConfigureAwait(false);

            foreach (var condition in policy.Conditions)
            {
                var input = ToInput(condition);
                if (existing.TryGetValue(condition.Name, out var current))
                {
                    if (Matches(current.Json, condition))
                    {
                        changes.Add(new Change(ChangeKind.Unchanged, "condition", condition.Name));
                        continue;
                    }
                    changes.Add(new Change(ChangeKind.Update, "condition", condition.Name));
                    if (!dryRun)
                        await UpdateConditionAsync(current.Id, input, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    changes.Add(new Change(ChangeKind.Create, "condition", condition.Name));
                    if (!dryRun)
                        await CreateConditionAsync(policyId!, input, cancellationToken).ConfigureAwait(false);
                }
            }

            var wanted = policy.Conditions.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, current) in existing)
            {
                if (wanted.Contains(name) || !IsManaged(current.Json))
                    continue;
                changes.Add(new Change(ChangeKind.Delete, "condition", name));
                if (!dryRun)
                    await DeleteConditionAsync(current.Id, cancellationToken).ConfigureAwait(false);
            }

            if (policy.Conditions.Count > 0 && channels.TryGetValue(policy.Severity, out var channelIds) && channelIds.Count > 0)
                changes.Add(await ProvisionWorkflowAsync(policy.Name, policyId, channelIds, dryRun, cancellationToken).ConfigureAwait(false));
        }

        return changes;
    }

    private async Task<string?> FindPolicyIdAsync(string name, CancellationToken cancellationToken)
    {
        const string query = """
            query($accountId: Int!, $cursor: String, $criteria: AlertsPoliciesSearchCriteriaInput) {
              actor { account(id: $accountId) { alerts {
                policiesSearch(cursor: $cursor, searchCriteria: $criteria) { nextCursor policies { id name } }
              } } }
            }
            """;
        string? cursor = null;
        do
        {
            var data = await _client.ExecuteAsync(query, new JsonObject
            {
                ["accountId"] = _accountId,
                ["cursor"] = cursor,
                ["criteria"] = new JsonObject { ["name"] = name },
            }, cancellationToken).ConfigureAwait(false);
            var search = data.GetProperty("actor").GetProperty("account").GetProperty("alerts").GetProperty("policiesSearch");

            // The name search ignores case; RunSignal's policy names are exact.
            foreach (var policy in search.GetProperty("policies").EnumerateArray())
            {
                if (policy.GetProperty("name").GetString() == name)
                    return IdOf(policy);
            }
            cursor = NextCursor(search);
        }
        while (cursor is not null);
        return null;
    }

    private async Task<string> CreatePolicyAsync(string name, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation($accountId: Int!, $policy: AlertsPolicyInput!) {
              alertsPolicyCreate(accountId: $accountId, policy: $policy) { id }
            }
            """;
        var data = await _client.ExecuteAsync(mutation, new JsonObject
        {
            ["accountId"] = _accountId,
            ["policy"] = new JsonObject { ["name"] = name, ["incidentPreference"] = "PER_CONDITION_AND_TARGET" },
        }, cancellationToken).ConfigureAwait(false);
        return IdOf(data.GetProperty("alertsPolicyCreate"));
    }

    private async Task<Dictionary<string, (string Id, JsonElement Json)>> ListConditionsAsync(string policyId, CancellationToken cancellationToken)
    {
        const string query = """
            query($accountId: Int!, $cursor: String, $criteria: AlertsNrqlConditionsSearchCriteriaInput) {
              actor { account(id: $accountId) { alerts {
                nrqlConditionsSearch(cursor: $cursor, searchCriteria: $criteria) {
                  nextCursor
                  nrqlConditions {
                    id name enabled description
                    nrql { query }
                    signal { aggregationWindow aggregationMethod aggregationDelay aggregationTimer }
                    terms { operator priority threshold thresholdDuration thresholdOccurrences }
                    expiration { expirationDuration openViolationOnExpiration }
                    violationTimeLimitSeconds
                  }
                }
              } } }
            }
            """;
        var conditions = new Dictionary<string, (string, JsonElement)>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var data = await _client.ExecuteAsync(query, new JsonObject
            {
                ["accountId"] = _accountId,
                ["cursor"] = cursor,
                ["criteria"] = new JsonObject { ["policyId"] = policyId },
            }, cancellationToken).ConfigureAwait(false);
            var search = data.GetProperty("actor").GetProperty("account").GetProperty("alerts").GetProperty("nrqlConditionsSearch");
            foreach (var condition in search.GetProperty("nrqlConditions").EnumerateArray())
                conditions[condition.GetProperty("name").GetString()!] = (IdOf(condition), condition.Clone());
            cursor = NextCursor(search);
        }
        while (cursor is not null);
        return conditions;
    }

    private Task CreateConditionAsync(string policyId, JsonObject condition, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation($accountId: Int!, $policyId: ID!, $condition: AlertsNrqlConditionStaticInput!) {
              alertsNrqlConditionStaticCreate(accountId: $accountId, policyId: $policyId, condition: $condition) { id }
            }
            """;
        return _client.ExecuteAsync(mutation, new JsonObject
        {
            ["accountId"] = _accountId,
            ["policyId"] = policyId,
            ["condition"] = condition,
        }, cancellationToken);
    }

    private Task UpdateConditionAsync(string id, JsonObject condition, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation($accountId: Int!, $id: ID!, $condition: AlertsNrqlConditionUpdateStaticInput!) {
              alertsNrqlConditionStaticUpdate(accountId: $accountId, id: $id, condition: $condition) { id }
            }
            """;
        return _client.ExecuteAsync(mutation, new JsonObject
        {
            ["accountId"] = _accountId,
            ["id"] = id,
            ["condition"] = condition,
        }, cancellationToken);
    }

    private Task DeleteConditionAsync(string id, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation($accountId: Int!, $id: ID!) {
              alertsConditionDelete(accountId: $accountId, id: $id) { id }
            }
            """;
        return _client.ExecuteAsync(mutation, new JsonObject { ["accountId"] = _accountId, ["id"] = id }, cancellationToken);
    }

    private async Task<Change> ProvisionWorkflowAsync(string name, string? policyId, IReadOnlyList<string> channelIds, bool dryRun, CancellationToken cancellationToken)
    {
        const string search = """
            query($accountId: Int!, $filters: AiWorkflowsFilters) {
              actor { account(id: $accountId) { aiWorkflows {
                workflows(filters: $filters) {
                  entities {
                    id name
                    issuesFilter { id predicates { attribute operator values } }
                    destinationConfigurations { channelId }
                  }
                }
              } } }
            }
            """;
        var data = await _client.ExecuteAsync(search, new JsonObject
        {
            ["accountId"] = _accountId,
            ["filters"] = new JsonObject { ["name"] = name },
        }, cancellationToken).ConfigureAwait(false);
        var existing = data.GetProperty("actor").GetProperty("account").GetProperty("aiWorkflows").GetProperty("workflows").GetProperty("entities")
            .EnumerateArray()
            .FirstOrDefault(w => w.GetProperty("name").GetString() == name);

        var wantedChannels = channelIds.Order(StringComparer.Ordinal).ToArray();
        if (existing.ValueKind == JsonValueKind.Undefined)
        {
            if (!dryRun)
                await CreateWorkflowAsync(name, policyId!, wantedChannels, cancellationToken).ConfigureAwait(false);
            return new Change(ChangeKind.Create, "workflow", name);
        }

        var filter = existing.GetProperty("issuesFilter");
        var currentChannels = existing.GetProperty("destinationConfigurations").EnumerateArray()
            .Select(d => d.GetProperty("channelId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var filtersPolicy = filter.GetProperty("predicates").EnumerateArray().Any(p =>
            p.GetProperty("attribute").GetString() == "labels.policyIds" &&
            p.GetProperty("values").EnumerateArray().Select(v => v.GetString()).SequenceEqual([policyId]));
        if (filtersPolicy && currentChannels.SequenceEqual(wantedChannels))
            return new Change(ChangeKind.Unchanged, "workflow", name);

        if (!dryRun)
            await UpdateWorkflowAsync(existing.GetProperty("id").GetString()!, filter.GetProperty("id").GetString()!, name, policyId!, wantedChannels, cancellationToken).ConfigureAwait(false);
        return new Change(ChangeKind.Update, "workflow", name);
    }

    private Task CreateWorkflowAsync(string name, string policyId, string[] channelIds, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation($accountId: Int!, $workflow: AiWorkflowsCreateWorkflowInput!) {
              aiWorkflowsCreateWorkflow(accountId: $accountId, createWorkflowData: $workflow) {
                workflow { id }
                errors { description type }
              }
            }
            """;
        return ExecuteWorkflowMutationAsync(mutation, "aiWorkflowsCreateWorkflow", new JsonObject
        {
            ["accountId"] = _accountId,
            ["workflow"] = new JsonObject
            {
                ["name"] = name,
                ["workflowEnabled"] = true,
                ["destinationsEnabled"] = true,
                ["enrichmentsEnabled"] = true,
                ["mutingRulesHandling"] = "DONT_NOTIFY_FULLY_MUTED_ISSUES",
                ["issuesFilter"] = PolicyFilter(name, policyId),
                ["destinationConfigurations"] = Destinations(channelIds),
            },
        }, cancellationToken);
    }

    private Task UpdateWorkflowAsync(string id, string filterId, string name, string policyId, string[] channelIds, CancellationToken cancellationToken)
    {
        const string mutation = """
            mutation($accountId: Int!, $workflow: AiWorkflowsUpdateWorkflowInput!) {
              aiWorkflowsUpdateWorkflow(accountId: $accountId, deleteUnusedChannels: false, updateWorkflowData: $workflow) {
                workflow { id }
                errors { description type }
              }
            }
            """;
        return ExecuteWorkflowMutationAsync(mutation, "aiWorkflowsUpdateWorkflow", new JsonObject
        {
            ["accountId"] = _accountId,
            ["workflow"] = new JsonObject
            {
                ["id"] = id,
                ["issuesFilter"] = new JsonObject { ["id"] = filterId, ["filterInput"] = PolicyFilter(name, policyId) },
                ["destinationConfigurations"] = Destinations(channelIds),
            },
        }, cancellationToken);
    }

    // Workflow mutations report validation problems in an errors field instead of GraphQL errors.
    private async Task ExecuteWorkflowMutationAsync(string mutation, string field, JsonObject variables, CancellationToken cancellationToken)
    {
        var data = await _client.ExecuteAsync(mutation, variables, cancellationToken).ConfigureAwait(false);
        if (data.GetProperty(field).TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray().Select(e => e.GetProperty("description").GetString());
            throw new NerdGraphException($"{field} failed: " + string.Join("; ", messages));
        }
    }

    private static JsonObject PolicyFilter(string name, string policyId) => new()
    {
        ["name"] = name,
        ["type"] = "FILTER",
        ["predicates"] = new JsonArray(new JsonObject
        {
            ["attribute"] = "labels.policyIds",
            ["operator"] = "EXACTLY_MATCHES",
            ["values"] = new JsonArray(policyId),
        }),
    };

    private static JsonArray Destinations(string[] channelIds) =>
        new(channelIds.Select(id => (JsonNode)new JsonObject
        {
            ["channelId"] = id,
            ["notificationTriggers"] = new JsonArray("ACTIVATED", "CLOSED"),
        }).ToArray());

    internal static JsonObject ToInput(AlertCondition condition)
    {
        var signal = new JsonObject
        {
            ["aggregationWindow"] = condition.AggregationWindowSeconds,
            ["aggregationMethod"] = MethodName(condition.AggregationMethod),
            ["fillOption"] = "NONE",
        };
        if (condition.AggregationMethod == AggregationMethod.EventFlow)
            signal["aggregationDelay"] = AggregationDelaySeconds;
        if (condition.AggregationTimerSeconds is { } timer)
            signal["aggregationTimer"] = timer;

        return new JsonObject
        {
            ["name"] = condition.Name,
            ["enabled"] = true,
            ["description"] = condition.Description,
            ["nrql"] = new JsonObject { ["query"] = condition.Nrql },
            ["signal"] = signal,
            ["terms"] = new JsonArray(new JsonObject
            {
                ["priority"] = "CRITICAL",
                ["operator"] = condition.Above ? "ABOVE" : "BELOW",
                ["threshold"] = condition.Threshold,
                ["thresholdDuration"] = condition.AggregationWindowSeconds,
                ["thresholdOccurrences"] = "AT_LEAST_ONCE",
            }),
            ["expiration"] = new JsonObject
            {
                ["expirationDuration"] = condition.LossOfSignalSeconds,
                ["openViolationOnExpiration"] = condition.LossOfSignalSeconds is not null,
                ["closeViolationsOnExpiration"] = false,
            },
            ["violationTimeLimitSeconds"] = ViolationTimeLimitSeconds,
        };
    }

    // Compares the fields RunSignal sets, so a condition is only updated when the plan really changed.
    internal static bool Matches(JsonElement current, AlertCondition wanted)
    {
        if (current.GetProperty("description").GetString() != wanted.Description ||
            !current.GetProperty("enabled").GetBoolean() ||
            current.GetProperty("nrql").GetProperty("query").GetString() != wanted.Nrql ||
            NumberOrNull(current, "violationTimeLimitSeconds") != ViolationTimeLimitSeconds)
            return false;

        var signal = current.GetProperty("signal");
        if (NumberOrNull(signal, "aggregationWindow") != wanted.AggregationWindowSeconds ||
            signal.GetProperty("aggregationMethod").GetString() != MethodName(wanted.AggregationMethod) ||
            (wanted.AggregationMethod == AggregationMethod.EventFlow && NumberOrNull(signal, "aggregationDelay") != AggregationDelaySeconds) ||
            (wanted.AggregationTimerSeconds is { } timer && NumberOrNull(signal, "aggregationTimer") != timer))
            return false;

        var terms = current.GetProperty("terms");
        if (terms.GetArrayLength() != 1)
            return false;
        var term = terms[0];
        if (term.GetProperty("priority").GetString() != "CRITICAL" ||
            term.GetProperty("operator").GetString() != (wanted.Above ? "ABOVE" : "BELOW") ||
            Math.Abs(term.GetProperty("threshold").GetDouble() - wanted.Threshold) > 1e-9 ||
            NumberOrNull(term, "thresholdDuration") != wanted.AggregationWindowSeconds ||
            term.GetProperty("thresholdOccurrences").GetString() != "AT_LEAST_ONCE")
            return false;

        var expiration = current.GetProperty("expiration");
        return NumberOrNull(expiration, "expirationDuration") == wanted.LossOfSignalSeconds &&
               expiration.GetProperty("openViolationOnExpiration").GetBoolean() == wanted.LossOfSignalSeconds is not null;
    }

    private static bool IsManaged(JsonElement condition) =>
        condition.GetProperty("description").GetString()?.StartsWith(AlertPlanner.ManagedMarker, StringComparison.Ordinal) == true;

    private static string MethodName(AggregationMethod method) => method == AggregationMethod.EventTimer ? "EVENT_TIMER" : "EVENT_FLOW";

    private static long? NumberOrNull(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? (long)value.GetDouble() : null;

    // NerdGraph returns IDs as strings, but older fields return numbers.
    private static string IdOf(JsonElement element)
    {
        var id = element.GetProperty("id");
        return id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString()!;
    }

    private static string? NextCursor(JsonElement search) =>
        search.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String && cursor.GetString() is { Length: > 0 } value
            ? value
            : null;
}
