using VeltrixControl.Contracts;

namespace VeltrixControl.Integrations;

public sealed record IntegrationConnection(string BaseUrl, string? Credential);

public interface IIntegrationAdapter
{
    string Kind { get; }

    Task<(bool Healthy, string Detail)> CheckHealthAsync(IntegrationConnection connection, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationResourceView>> ListResourcesAsync(IntegrationConnection connection, CancellationToken cancellationToken);

    Task<string> ExecuteAsync(IntegrationConnection connection, string action, string resourceId, CancellationToken cancellationToken);

    Task<string> GetLogsAsync(IntegrationConnection connection, string resourceId, CancellationToken cancellationToken);
}

public static class IntegrationHttp
{
    public static HttpClient CreateClient(IntegrationConnection connection, out Uri? baseAddress)
    {
        baseAddress = null;
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var url = connection.BaseUrl.Trim();

        if (url.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            var pipeName = url["npipe://".Length..].Replace('/', '\\');
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName.Replace(@"\\.\pipe\", string.Empty), System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, cancellationToken);
                return pipe;
            };
            return new HttpClient(handler) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromSeconds(20) };
        }

        if (url.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url["tcp://".Length..];
        }

        var uri = new Uri(url, UriKind.Absolute);
        baseAddress = uri;
        return new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };
    }

    public static void ApplyCredential(HttpRequestMessage request, IntegrationConnection connection, bool bearer = true)
    {
        if (string.IsNullOrWhiteSpace(connection.Credential)) return;
        if (bearer && request.Headers.Authorization is null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.Credential);
        }
        else if (!bearer)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", connection.Credential);
        }
    }

    public static void ApplyDockerCredential(HttpRequestMessage request, IntegrationConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Credential)) return;
        request.Headers.TryAddWithoutValidation("X-Registry-Auth", connection.Credential);
    }
}
