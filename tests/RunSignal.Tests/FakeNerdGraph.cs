using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace RunSignal.Tests;

/// <summary>
/// An in-memory stand-in for New Relic's NerdGraph that keeps policies, NRQL conditions and workflows, so
/// provisioning can be tested end to end, including idempotency. It answers only the operations RunSignal sends.
/// </summary>
internal sealed class FakeNerdGraph : HttpMessageHandler
{
    private int _nextId = 1000;

    public Dictionary<string, string> Policies { get; } = new();                 // id -> name
    public Dictionary<string, JsonObject> Conditions { get; } = new();          // id -> condition (with policyId)
    public Dictionary<string, JsonObject> Workflows { get; } = new();           // id -> workflow
    public Dictionary<string, JsonObject> Dashboards { get; } = new();          // guid -> dashboard

    /// <summary>How many entity searches miss a newly created dashboard, like New Relic's search index lag.</summary>
    public int SearchLag { get; set; }

    private readonly Dictionary<string, int> _searchMisses = new();
    public List<string> Operations { get; } = new();
    public string? LastApiKey { get; private set; }
    public Uri? LastUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastApiKey = request.Headers.GetValues("API-Key").Single();
        LastUri = request.RequestUri;
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var query = body["query"]!.GetValue<string>();
        var variables = body["variables"]!.AsObject();

        var data = Handle(query, variables);
        var json = new JsonObject { ["data"] = data }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private JsonObject Handle(string query, JsonObject v)
    {
        if (query.Contains("policiesSearch"))
        {
            Operations.Add("policiesSearch");
            var name = v["criteria"]!["name"]!.GetValue<string>();
            var policies = new JsonArray(Policies.Where(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase))
                .Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["name"] = p.Value }).ToArray());
            return Account("alerts", new JsonObject { ["policiesSearch"] = new JsonObject { ["nextCursor"] = null, ["policies"] = policies } });
        }

        if (query.Contains("alertsPolicyCreate"))
        {
            Operations.Add("alertsPolicyCreate");
            var id = NextId();
            Policies[id] = v["policy"]!["name"]!.GetValue<string>();
            return new JsonObject { ["alertsPolicyCreate"] = new JsonObject { ["id"] = id } };
        }

        if (query.Contains("nrqlConditionsSearch"))
        {
            Operations.Add("nrqlConditionsSearch");
            var policyId = v["criteria"]!["policyId"]!.GetValue<string>();
            var conditions = new JsonArray(Conditions.Where(c => c.Value["policyId"]!.GetValue<string>() == policyId)
                .Select(c => c.Value.DeepClone()).ToArray());
            return Account("alerts", new JsonObject { ["nrqlConditionsSearch"] = new JsonObject { ["nextCursor"] = null, ["nrqlConditions"] = conditions } });
        }

        if (query.Contains("alertsNrqlConditionStaticCreate"))
        {
            Operations.Add("create " + v["condition"]!["name"]);
            var id = NextId();
            var condition = v["condition"]!.DeepClone().AsObject();
            condition["id"] = id;
            condition["policyId"] = v["policyId"]!.GetValue<string>();
            Conditions[id] = condition;
            return new JsonObject { ["alertsNrqlConditionStaticCreate"] = new JsonObject { ["id"] = id } };
        }

        if (query.Contains("alertsNrqlConditionStaticUpdate"))
        {
            var id = v["id"]!.GetValue<string>();
            Operations.Add("update " + v["condition"]!["name"]);
            var condition = v["condition"]!.DeepClone().AsObject();
            condition["id"] = id;
            condition["policyId"] = Conditions[id]["policyId"]!.GetValue<string>();
            Conditions[id] = condition;
            return new JsonObject { ["alertsNrqlConditionStaticUpdate"] = new JsonObject { ["id"] = id } };
        }

        if (query.Contains("alertsConditionDelete"))
        {
            var id = v["id"]!.GetValue<string>();
            Operations.Add("delete " + Conditions[id]["name"]);
            Conditions.Remove(id);
            return new JsonObject { ["alertsConditionDelete"] = new JsonObject { ["id"] = id } };
        }

        if (query.Contains("dashboardCreate"))
        {
            var dashboard = v["dashboard"]!.DeepClone().AsObject();
            var guid = AddDashboard(dashboard);
            Operations.Add("create dashboard " + dashboard["name"]);
            _searchMisses[guid] = SearchLag;
            return new JsonObject { ["dashboardCreate"] = new JsonObject { ["entityResult"] = new JsonObject { ["guid"] = guid }, ["errors"] = new JsonArray() } };
        }

        if (query.Contains("dashboardUpdate"))
        {
            var guid = v["guid"]!.GetValue<string>();
            Operations.Add("update dashboard " + Dashboards[guid]["name"]);
            Dashboards[guid] = v["dashboard"]!.DeepClone().AsObject();
            return new JsonObject { ["dashboardUpdate"] = new JsonObject { ["entityResult"] = new JsonObject { ["guid"] = guid }, ["errors"] = new JsonArray() } };
        }

        if (query.Contains("dashboardDelete"))
        {
            var guid = v["guid"]!.GetValue<string>();
            Operations.Add("delete dashboard " + Dashboards[guid]["name"]);
            Dashboards.Remove(guid);
            return new JsonObject { ["dashboardDelete"] = new JsonObject { ["status"] = "SUCCESS", ["errors"] = new JsonArray() } };
        }

        if (query.Contains("entitySearch"))
        {
            Operations.Add("entitySearch");
            var name = System.Text.RegularExpressions.Regex.Match(v["query"]!.GetValue<string>(), "name = '(.*)'$").Groups[1].Value;
            var entities = new JsonArray();
            foreach (var (guid, dashboard) in Dashboards)
            {
                if (dashboard["name"]!.GetValue<string>() != name)
                    continue;
                if (_searchMisses.TryGetValue(guid, out var misses) && misses > 0)
                {
                    _searchMisses[guid] = misses - 1;
                    continue;
                }
                entities.Add(new JsonObject { ["guid"] = guid, ["name"] = name, ["dashboardParentGuid"] = null });
                // Pages are entities too, with the dashboard as their parent.
                entities.Add(new JsonObject { ["guid"] = guid + "-page", ["name"] = name, ["dashboardParentGuid"] = guid });
            }
            return new JsonObject { ["actor"] = new JsonObject { ["entitySearch"] = new JsonObject { ["results"] = new JsonObject { ["entities"] = entities } } } };
        }

        if (query.Contains("entity(guid"))
        {
            var guid = v["guid"]!.GetValue<string>();
            if (!Dashboards.TryGetValue(guid, out var dashboard))
                return new JsonObject { ["actor"] = new JsonObject { ["entity"] = null } };
            var pages = new JsonArray(dashboard["pages"]!.AsArray().Select(page => (JsonNode)new JsonObject
            {
                ["widgets"] = new JsonArray(page!["widgets"]!.AsArray().Select(w => (JsonNode)new JsonObject
                {
                    ["title"] = w!["title"]!.DeepClone(),
                    ["visualization"] = w["visualization"]!.DeepClone(),
                    ["layout"] = w["layout"]!.DeepClone(),
                    // New Relic adds its own settings to the raw configuration.
                    ["rawConfiguration"] = new JsonObject
                    {
                        ["nrqlQueries"] = w["rawConfiguration"]!["nrqlQueries"]!.DeepClone(),
                        ["platformOptions"] = new JsonObject { ["ignoreTimeRange"] = false },
                    },
                }).ToArray()),
            }).ToArray());
            return new JsonObject { ["actor"] = new JsonObject { ["entity"] = new JsonObject { ["description"] = dashboard["description"]!.DeepClone(), ["pages"] = pages } } };
        }

        if (query.Contains("alertsPolicyDelete"))
        {
            var id = v["id"]!.GetValue<string>();
            Operations.Add("delete policy " + Policies[id]);
            Policies.Remove(id);
            return new JsonObject { ["alertsPolicyDelete"] = new JsonObject { ["id"] = id } };
        }

        if (query.Contains("aiWorkflowsDeleteWorkflow"))
        {
            var id = v["id"]!.GetValue<string>();
            if (!query.Contains("deleteChannels: false"))
                throw new InvalidOperationException("RunSignal must never delete notification channels.");
            Operations.Add("delete workflow " + Workflows[id]["name"]);
            Workflows.Remove(id);
            return new JsonObject { ["aiWorkflowsDeleteWorkflow"] = new JsonObject { ["id"] = id, ["errors"] = new JsonArray() } };
        }

        if (query.Contains("aiWorkflowsCreateWorkflow"))
        {
            var input = v["workflow"]!.AsObject();
            Operations.Add("create workflow " + input["name"]);
            var id = NextId();
            Workflows[id] = new JsonObject
            {
                ["id"] = id,
                ["name"] = input["name"]!.GetValue<string>(),
                ["issuesFilter"] = Filter(NextId(), input["issuesFilter"]!.AsObject()),
                ["destinationConfigurations"] = input["destinationConfigurations"]!.DeepClone(),
            };
            return new JsonObject { ["aiWorkflowsCreateWorkflow"] = new JsonObject { ["workflow"] = new JsonObject { ["id"] = id }, ["errors"] = new JsonArray() } };
        }

        if (query.Contains("aiWorkflowsUpdateWorkflow"))
        {
            var input = v["workflow"]!.AsObject();
            var id = input["id"]!.GetValue<string>();
            Operations.Add("update workflow " + Workflows[id]["name"]);
            var filter = input["issuesFilter"]!.AsObject();
            Workflows[id]["issuesFilter"] = Filter(filter["id"]!.GetValue<string>(), filter["filterInput"]!.AsObject());
            Workflows[id]["destinationConfigurations"] = input["destinationConfigurations"]!.DeepClone();
            return new JsonObject { ["aiWorkflowsUpdateWorkflow"] = new JsonObject { ["workflow"] = new JsonObject { ["id"] = id }, ["errors"] = new JsonArray() } };
        }

        if (query.Contains("aiWorkflows"))
        {
            Operations.Add("workflowsSearch");
            var name = v["filters"]!["name"]!.GetValue<string>();
            var entities = new JsonArray(Workflows.Values.Where(w => w["name"]!.GetValue<string>() == name).Select(w => w.DeepClone()).ToArray());
            return Account("aiWorkflows", new JsonObject { ["workflows"] = new JsonObject { ["entities"] = entities } });
        }

        throw new InvalidOperationException("Unexpected NerdGraph operation: " + query);
    }

    public void AddUnmanagedCondition(string policyId, string name)
    {
        var id = NextId();
        Conditions[id] = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["policyId"] = policyId,
            ["description"] = "Created by hand.",
        };
    }

    /// <summary>Stores a dashboard as if it existed already, and returns its guid.</summary>
    public string AddDashboard(JsonObject dashboard)
    {
        var guid = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"42|VIZ|DASHBOARD|da:{_nextId++}")).TrimEnd('=');
        Dashboards[guid] = dashboard;
        return guid;
    }

    private static JsonObject Filter(string id, JsonObject input) => new()
    {
        ["id"] = id,
        ["predicates"] = input["predicates"]!.DeepClone(),
    };

    private static JsonObject Account(string section, JsonObject content) =>
        new() { ["actor"] = new JsonObject { ["account"] = new JsonObject { [section] = content } } };

    private string NextId() => (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
