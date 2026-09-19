using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
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

    public Task<TransferView> CreateTransferAsync(Guid deviceId, TransferRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TransferView>(HttpMethod.Post, $"api/devices/{deviceId:D}/transfers", request, true, cancellationToken);

    public Task<TransferView> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default) =>
        GetAsync<TransferView>($"api/transfers/{transferId:D}", cancellationToken);

    public Task<TransferView[]> GetTransfersAsync(Guid deviceId, bool activeOnly, CancellationToken cancellationToken = default) =>
        GetAsync<TransferView[]>($"api/devices/{deviceId:D}/transfers?active={(activeOnly ? "true" : "false")}&limit=100", cancellationToken);

    public async Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Delete, $"api/transfers/{transferId:D}", null, true, cancellationToken);

    public Task<TerminalStartResponse> StartTerminalAsync(Guid deviceId, StartTerminalRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TerminalStartResponse>(HttpMethod.Post, $"api/devices/{deviceId:D}/terminal", request, true, cancellationToken);

    public Task<TerminalOperationResponse> TerminalInputAsync(Guid sessionId, string data, CancellationToken cancellationToken = default) =>
        SendAsync<TerminalOperationResponse>(HttpMethod.Post, $"api/terminal/{sessionId:D}/input", new TerminalInputRequest(data), true, cancellationToken);

    public Task<TerminalOperationResponse> TerminalOutputAsync(Guid sessionId, long since, CancellationToken cancellationToken = default) =>
        SendAsync<TerminalOperationResponse>(HttpMethod.Post, $"api/terminal/{sessionId:D}/output?since={since}", null, true, cancellationToken);

    public Task<TerminalOperationResponse> StopTerminalAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAsync<TerminalOperationResponse>(HttpMethod.Post, $"api/terminal/{sessionId:D}/stop", null, true, cancellationToken);

    public async Task WakeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"api/devices/{deviceId:D}/wake", null, true, cancellationToken);

    public Task<SoftwarePackageView[]> GetSoftwarePackagesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SoftwarePackageView[]>("api/software/packages", cancellationToken);

    public Task<SoftwarePackageView> CreateSoftwarePackageAsync(SoftwarePackageRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SoftwarePackageView>(HttpMethod.Post, "api/software/packages", request, true, cancellationToken);

    public Task<SoftwareDeploymentView[]> GetSoftwareDeploymentsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SoftwareDeploymentView[]>("api/software/deployments?limit=100", cancellationToken);

    public Task<SoftwareDeploymentView> GetSoftwareDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken = default) =>
        GetAsync<SoftwareDeploymentView>($"api/software/deployments/{deploymentId:D}", cancellationToken);

    public Task<SoftwareDeploymentView> CreateSoftwareDeploymentAsync(SoftwareDeploymentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SoftwareDeploymentView>(HttpMethod.Post, "api/software/deployments", request, true, cancellationToken);

    public async Task CancelSoftwareDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"api/software/deployments/{deploymentId:D}/cancel", null, true, cancellationToken);

    public async Task<WindowsUpdateScanResult?> GetWindowsUpdateScanAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync($"api/devices/{deviceId:D}/updates", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) throw new ControllerApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<WindowsUpdateScanResult>(_json, cancellationToken);
    }

    public Task<OperationView> ScanWindowsUpdatesAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationView>(HttpMethod.Post, $"api/devices/{deviceId:D}/updates/scan", null, true, cancellationToken);

    public Task<OperationView> InstallWindowsUpdatesAsync(Guid deviceId, string[] updateIds, CancellationToken cancellationToken = default) =>
        SendAsync<OperationView>(HttpMethod.Post, $"api/devices/{deviceId:D}/updates/install", new WindowsUpdateInstallArgument(updateIds), true, cancellationToken);

    public Task<BackupView[]> GetBackupsAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        GetAsync<BackupView[]>($"api/devices/{deviceId:D}/backups", cancellationToken);

    public Task<JsonElement> CreateBackupAsync(Guid deviceId, BackupRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"api/devices/{deviceId:D}/backups", request, true, cancellationToken);

    public Task<OperationView> RestoreBackupAsync(Guid backupId, string destinationPath, CancellationToken cancellationToken = default) =>
        SendAsync<OperationView>(HttpMethod.Post, $"api/backups/{backupId:D}/restore", new RestoreBackupArgument("server-managed", destinationPath), true, cancellationToken);

    public Task<OperationView> VerifyBackupAsync(Guid backupId, CancellationToken cancellationToken = default) =>
        SendAsync<OperationView>(HttpMethod.Post, $"api/backups/{backupId:D}/verify", null, true, cancellationToken);

    public Task<AlertView[]> GetAlertsAsync(string? state = null, CancellationToken cancellationToken = default) =>
        GetAsync<AlertView[]>($"api/alerts{(string.IsNullOrWhiteSpace(state) ? string.Empty : $"?state={state}")}", cancellationToken);

    public async Task AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"api/alerts/{alertId:D}/acknowledge", null, true, cancellationToken);

    public Task<AutomationView[]> GetAutomationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AutomationView[]>("api/automations", cancellationToken);

    public Task<AutomationView> CreateAutomationAsync(AutomationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AutomationView>(HttpMethod.Post, "api/automations", request, true, cancellationToken);

    public async Task SetAutomationEnabledAsync(Guid automationId, bool enabled, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"api/automations/{automationId:D}/{(enabled ? "enable" : "disable")}", null, true, cancellationToken);

    public async Task DeleteAutomationAsync(Guid automationId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Delete, $"api/automations/{automationId:D}", null, true, cancellationToken);

    public Task<AutomationRunView[]> GetAutomationRunsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AutomationRunView[]>("api/automations/runs?limit=100", cancellationToken);

    public Task<ComputePolicyView> GetComputePolicyAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        GetAsync<ComputePolicyView>($"api/devices/{deviceId:D}/compute-policy", cancellationToken);

    public Task<ComputePolicyView> SetComputePolicyAsync(Guid deviceId, ComputePolicyRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ComputePolicyView>(HttpMethod.Put, $"api/devices/{deviceId:D}/compute-policy", request, true, cancellationToken);

    public Task<ComputeJobView[]> GetComputeJobsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ComputeJobView[]>("api/compute/jobs?limit=200", cancellationToken);

    public Task<ComputeJobView> CreateComputeJobAsync(ComputeJobRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ComputeJobView>(HttpMethod.Post, "api/compute/jobs", request, true, cancellationToken);

    public async Task CancelComputeJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await SendAsync<object?>(HttpMethod.Post, $"api/compute/jobs/{jobId:D}/cancel", null, true, cancellationToken);

    public async Task<TransferView> UploadFileAsync(Guid deviceId, string localPath, string remotePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = new FileInfo(localPath);
        var sha = ComputeSha256(localPath);
        var transfer = await CreateTransferAsync(deviceId, new TransferRequest(TransferDirection.Upload, remotePath, info.Length, sha), cancellationToken);
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 512 * 1024, useAsync: true);
        var buffer = new byte[512 * 1024];
        long offset = 0;
        while (offset < info.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, info.Length - offset)), cancellationToken);
            if (read <= 0) break;
            using var request = new HttpRequestMessage(HttpMethod.Put, $"api/transfers/{transfer.Id:D}/chunks?offset={offset}")
            {
                Content = new ByteArrayContent(buffer, 0, read)
            };
            request.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadErrorAsync(response, cancellationToken);
                throw new ControllerApiException(response.StatusCode, message);
            }
            offset += read;
            progress?.Report(info.Length == 0 ? 1 : (double)offset / info.Length);
        }
        return await GetTransferAsync(transfer.Id, cancellationToken);
    }

    public async Task DownloadFileAsync(TransferView transfer, string localPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var current = transfer;
        while (current.State is TransferState.Pending)
        {
            await Task.Delay(750, cancellationToken);
            current = await GetTransferAsync(transfer.Id, cancellationToken);
        }

        var temporary = localPath + ".veltrix-download";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 512 * 1024, useAsync: true))
        {
            long offset = 0;
            while (offset < current.TotalBytes || (current.State == TransferState.Completed && current.TotalBytes == 0))
            {
                var chunk = await GetBytesAsync($"api/transfers/{transfer.Id:D}/chunks?offset={offset}&count={512 * 1024}", cancellationToken);
                if (chunk.Length == 0) break;
                await stream.WriteAsync(chunk, cancellationToken);
                offset += chunk.Length;
                progress?.Report(current.TotalBytes == 0 ? 1 : (double)offset / current.TotalBytes);
            }
            await stream.FlushAsync(cancellationToken);
        }

        current = await GetTransferAsync(transfer.Id, cancellationToken);
        if (current.State != TransferState.Completed && current.State != TransferState.Active)
            throw new ControllerApiException(HttpStatusCode.Conflict, current.Error ?? $"Transfer ended with {current.State}.");
        File.Move(temporary, localPath, true);
    }

    private async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ControllerApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(_json, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Error)) return error.Error;
        }
        catch (JsonException)
        {
        }
        return $"Controller returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

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
