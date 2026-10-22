using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunSignal.NewRelic;

/// <summary>The New Relic data center an account lives in.</summary>
public enum NewRelicRegion
{
    /// <summary>United States.</summary>
    US,

    /// <summary>European Union.</summary>
    EU,

    /// <summary>Japan.</summary>
    JP,
}

/// <summary>A NerdGraph request that returned errors.</summary>
public sealed class NerdGraphException(string message) : Exception(message);

/// <summary>A minimal client for New Relic's NerdGraph (GraphQL) API, authenticated with a User API key.</summary>
public sealed class NerdGraphClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Uri _endpoint;

    /// <summary>Creates a client that sends requests with <paramref name="http"/>.</summary>
    public NerdGraphClient(HttpClient http, string apiKey, NewRelicRegion region = NewRelicRegion.US)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _http = http;
        _apiKey = apiKey;
        _endpoint = new Uri(region switch
        {
            NewRelicRegion.EU => "https://api.eu.newrelic.com/graphql",
            NewRelicRegion.JP => "https://api.jp.newrelic.com/graphql",
            _ => "https://api.newrelic.com/graphql",
        });
    }

    /// <summary>Runs a query or mutation and returns its <c>data</c> element.</summary>
    /// <exception cref="NerdGraphException">The response contains GraphQL errors.</exception>
    public async Task<JsonElement> ExecuteAsync(string document, JsonObject variables, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["query"] = document, ["variables"] = variables };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("API-Key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new NerdGraphException($"NerdGraph returned HTTP {(int)response.StatusCode}: {Truncate(text)}");

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            // The message alone is often just "Validation Error"; the details are in extensions and path.
            var messages = errors.EnumerateArray().Select(e =>
            {
                var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                var details = new List<string>();
                if (e.TryGetProperty("path", out var path))
                    details.Add("at " + string.Join('.', path.EnumerateArray().Select(p => p.ToString())));
                if (e.TryGetProperty("extensions", out var extensions))
                    details.Add(Truncate(extensions.GetRawText()));
                return details.Count == 0 ? message ?? e.GetRawText() : $"{message} ({string.Join(", ", details)})";
            });
            throw new NerdGraphException("NerdGraph returned errors: " + string.Join("; ", messages));
        }

        return root.GetProperty("data").Clone();
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
