using System.Net;
using System.Text.Json;
using VeltrixControl.Contracts;

namespace VeltrixControl.Integrations;

public sealed class DockerAdapter : IIntegrationAdapter
{
    public string Kind => "Docker";

    public async Task<(bool Healthy, string Detail)> CheckHealthAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        using var client = IntegrationHttp.CreateClient(connection, out _);
        using var response = await client.GetAsync("_ping", cancellationToken);
        if (response.IsSuccessStatusCode) return (true, "Docker engine responded to ping.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (false, "The Docker endpoint rejected the request.");
        return (false, $"Docker returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public async Task<IReadOnlyList<IntegrationResourceView>> ListResourcesAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var resources = new List<IntegrationResourceView>();
        using var client = IntegrationHttp.CreateClient(connection, out _);

        using (var response = await client.GetAsync("containers/json?all=1", cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var item in document.RootElement.EnumerateArray().Take(IntegrationLimits.MaxResources))
            {
                var id = item.TryGetProperty("Id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                var names = item.TryGetProperty("Names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", namesElement.EnumerateArray().Select(name => name.GetString()?.TrimStart('/')))
                    : id[..Math.Min(12, id.Length)];
                var status = item.TryGetProperty("State", out var stateElement) ? stateElement.GetString() ?? "unknown" : "unknown";
                var detail = item.TryGetProperty("Status", out var statusElement) ? statusElement.GetString() ?? string.Empty : string.Empty;
                resources.Add(new IntegrationResourceView($"container:{id}", names, status, detail));
            }
        }

        using (var response = await client.GetAsync("images/json", cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                foreach (var item in document.RootElement.EnumerateArray().Take(200))
                {
                    var id = item.TryGetProperty("Id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                    var tags = item.TryGetProperty("RepoTags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", tagsElement.EnumerateArray().Select(tag => tag.GetString()))
                        : id[..Math.Min(12, id.Length)];
                    var size = item.TryGetProperty("Size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
                    resources.Add(new IntegrationResourceView($"image:{id}", tags, "image", $"{size / (1024 * 1024)} MB"));
                }
            }
        }

        using (var response = await client.GetAsync("volumes", cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("Volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in volumes.EnumerateArray().Take(200))
                    {
                        var name = item.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                        var driver = item.TryGetProperty("Driver", out var driverElement) ? driverElement.GetString() ?? string.Empty : string.Empty;
                        resources.Add(new IntegrationResourceView($"volume:{name}", name, "volume", driver));
                    }
                }
            }
        }

        return resources;
    }

    public async Task<string> ExecuteAsync(IntegrationConnection connection, string action, string resourceId, CancellationToken cancellationToken)
    {
        if (!resourceId.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Docker containers support lifecycle actions through this integration.");
        var id = resourceId["container:".Length..];
        if (id.Length is < 12 or > 64 || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("The container identifier is invalid.");
        var verb = action.ToLowerInvariant() switch
        {
            "start" => "start",
            "stop" => "stop",
            "restart" => "restart",
            _ => throw new ArgumentException($"Action '{action}' is not supported.")
        };
        using var client = IntegrationHttp.CreateClient(connection, out _);
        using var response = await client.PostAsync($"containers/{id}/{verb}?t=15", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return $"Container was already in the requested state for '{verb}'.";
        response.EnsureSuccessStatusCode();
        return $"{verb} accepted.";
    }

    public async Task<string> GetLogsAsync(IntegrationConnection connection, string resourceId, CancellationToken cancellationToken)
    {
        if (!resourceId.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Docker containers expose logs through this integration.");
        var id = resourceId["container:".Length..];
        if (id.Length is < 12 or > 64 || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("The container identifier is invalid.");
        using var client = IntegrationHttp.CreateClient(connection, out _);
        using var response = await client.GetAsync($"containers/{id}/logs?stdout=1&stderr=1&tail=200", cancellationToken);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var text = System.Text.Encoding.UTF8.GetString(raw);
        return text.Length > 32 * 1024 ? text[^(32 * 1024)..] : text;
    }
}
