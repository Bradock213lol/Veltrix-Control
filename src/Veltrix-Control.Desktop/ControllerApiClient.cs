using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public sealed class ControllerApiClient : IDisposable
{
    private CookieContainer _cookies = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private HttpClient _client;

    public ControllerApiClient(string controllerUrl) => _client = CreateClient(ValidateControllerUri(controllerUrl));

    public Uri BaseAddress => _client.BaseAddress!;

    public void ChangeController(string controllerUrl)
    {
        var controller = ValidateControllerUri(controllerUrl);
        if (!string.Equals(_client.BaseAddress?.Authority, controller.Authority, StringComparison.OrdinalIgnoreCase))
            _cookies = new CookieContainer();
        var replacement = CreateClient(controller);
        var previous = Interlocked.Exchange(ref _client, replacement);
        previous.Dispose();
    }

    public Task<SetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SetupStatus>("api/setup/status", cancellationToken);

    public Task<UserIdentity> SetupAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<UserIdentity>(HttpMethod.Post, "api/setup", new SetupRequest(username, password), false, cancellationToken);

    public Task<UserIdentity> LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<UserIdentity>(HttpMethod.Post, "api/auth/login", new LoginRequest(username, password), false, cancellationToken);

    public Task<UserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        GetAsync<UserIdentity>("api/auth/me", cancellationToken);

    public async Task LogoutAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, "api/auth/logout", null, true, cancellationToken);

    public Task<DeviceSummary[]> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DeviceSummary[]>("api/devices", cancellationToken);

    public Task<ControllerInfo> GetControllerInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ControllerInfo>("api/controller/info", cancellationToken);

    public Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(int lifetimeMinutes, CancellationToken cancellationToken = default) =>
        SendAsync<EnrollmentTokenResponse>(HttpMethod.Post, "api/enrollment/tokens", new CreateEnrollmentTokenRequest(lifetimeMinutes), true, cancellationToken);

    public async Task RevokeEnrollmentTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"api/enrollment/tokens/{tokenId:D}/revoke", null, true, cancellationToken);

    public Task<AuditEventView[]> GetAuditAsync(int limit = 500, CancellationToken cancellationToken = default) =>
        GetAsync<AuditEventView[]>($"api/audit?limit={Math.Clamp(limit, 1, 1000)}", cancellationToken);

    public Task<AuditIntegrity> VerifyAuditAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AuditIntegrity>("api/audit/integrity", cancellationToken);

    public Task<OperationView> CreateOperationAsync(Guid deviceId, OperationKind kind, string? argument, bool confirmed, CancellationToken cancellationToken = default) =>
        SendAsync<OperationView>(HttpMethod.Post, $"api/devices/{deviceId:D}/operations", new OperationRequest(kind, argument, confirmed), true, cancellationToken);

    public Task<OperationView> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        GetAsync<OperationView>($"api/operations/{operationId:D}", cancellationToken);

    public async Task<OperationView> WaitForOperationAsync(Guid operationId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(600, cancellationToken);
            var operation = await GetOperationAsync(operationId, cancellationToken);
            if (operation.State is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.TimedOut)
                return operation;
        }
        throw new TimeoutException("The managed node did not finish the request in time.");
    }

    public static Uri ValidateControllerUri(string controllerUrl)
    {
        if (!Uri.TryCreate(controllerUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid HTTP or HTTPS Controller URL.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new ArgumentException("Remote Controllers require HTTPS. HTTP is allowed only for local loopback.");
        return new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/");
    }

    private HttpClient CreateClient(Uri baseAddress) => new(new HttpClientHandler
    {
        CookieContainer = _cookies,
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(15)
    };

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool verified, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: _json);
        if (verified) request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (typeof(T) == typeof(object) && response.IsSuccessStatusCode) return default!;
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<T>(_json, cancellationToken))!;

        var message = $"Controller returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(_json, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Error)) message = error.Error;
        }
        catch (JsonException)
        {
            // Use the status-based message when the response body is not JSON.
        }
        throw new ControllerApiException(response.StatusCode, message);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class ControllerApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
