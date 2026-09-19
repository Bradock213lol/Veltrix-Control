using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VeltrixControl.Contracts;

namespace VeltrixControl.Integrations;

public sealed class PterodactylAdapter : IIntegrationAdapter
{
    public string Kind => "Pterodactyl";

    public async Task<(bool Healthy, string Detail)> CheckHealthAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        using var client = IntegrationHttp.CreateClient(connection, out _);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/application/nodes?per_page=1");
        IntegrationHttp.ApplyCredential(request, connection);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return (true, $"Panel responded {(int)response.StatusCode}.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (false, "The Pterodactyl application API key was rejected.");
        return (false, $"Panel returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public async Task<IReadOnlyList<IntegrationResourceView>> ListResourcesAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var resources = new List<IntegrationResourceView>();
        using var client = IntegrationHttp.CreateClient(connection, out _);
        resources.AddRange(await ReadNodesAsync(client, connection, cancellationToken));
        resources.AddRange(await ReadServersAsync(client, connection, cancellationToken));
        return resources;
    }

    private static async Task<IReadOnlyList<IntegrationResourceView>> ReadNodesAsync(HttpClient client, IntegrationConnection connection, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/application/nodes?per_page=100");
        IntegrationHttp.ApplyCredential(request, connection);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var resources = new List<IntegrationResourceView>();
        if (!document.RootElement.TryGetProperty("data", out var data)) return resources;
        foreach (var item in data.EnumerateArray().Take(IntegrationLimits.MaxResources))
        {
            if (!item.TryGetProperty("attributes", out var attributes)) continue;
            var id = attributes.TryGetProperty("id", out var idElement) ? idElement.ToString() : string.Empty;
            var name = attributes.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? id : id;
            resources.Add(new IntegrationResourceView($"node:{id}", name, "node", "Pterodactyl node"));
        }
        return resources;
    }

    private static async Task<IReadOnlyList<IntegrationResourceView>> ReadServersAsync(HttpClient client, IntegrationConnection connection, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/application/servers?per_page=100");
        IntegrationHttp.ApplyCredential(request, connection);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var resources = new List<IntegrationResourceView>();
        if (!document.RootElement.TryGetProperty("data", out var data)) return resources;
        foreach (var item in data.EnumerateArray().Take(IntegrationLimits.MaxResources))
        {
            if (!item.TryGetProperty("attributes", out var attributes)) continue;
            var id = attributes.TryGetProperty("id", out var idElement) ? idElement.ToString() : string.Empty;
            var name = attributes.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? id : id;
            var identifier = attributes.TryGetProperty("identifier", out var identifierElement) ? identifierElement.GetString() : null;
            var status = attributes.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            resources.Add(new IntegrationResourceView($"server:{id}", name, status ?? "unknown", identifier ?? string.Empty));
        }
        return resources;
    }

    public async Task<string> ExecuteAsync(IntegrationConnection connection, string action, string resourceId, CancellationToken cancellationToken)
    {
        if (!resourceId.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Pterodactyl servers support power actions through this integration.");
        var id = resourceId["server:".Length..];
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) throw new ArgumentException("The server identifier is invalid.");

        var signal = action.ToLowerInvariant() switch
        {
            "start" => "start",
            "stop" => "stop",
            "restart" => "restart",
            _ => throw new ArgumentException($"Action '{action}' is not supported.")
        };
        using var client = IntegrationHttp.CreateClient(connection, out _);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/application/servers/{id}/power")
        {
            Content = JsonContent.Create(new { signal })
        };
        IntegrationHttp.ApplyCredential(request, connection);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return $"Signal '{signal}' accepted.";
        response.EnsureSuccessStatusCode();
        return $"Signal '{signal}' sent.";
    }

    public Task<string> GetLogsAsync(IntegrationConnection connection, string resourceId, CancellationToken cancellationToken)
    {
        _ = connection;
        _ = resourceId;
        _ = cancellationToken;
        return Task.FromResult("The Pterodactyl application API does not expose live console logs; use the Panel or a client API key with websocket access.");
    }
}
