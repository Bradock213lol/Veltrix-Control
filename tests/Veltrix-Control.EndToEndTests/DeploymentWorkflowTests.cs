using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class DeploymentWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SoftwareDeploymentCompletesThroughTheAgent()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "deploy.owner");

        var package = await PostUiAsync<SoftwarePackageView>(client, "/api/software/packages",
            new SoftwarePackageRequest("7-Zip", SoftwareSource.Winget, "7zip.7zip", null, null, null));
        var deployment = await PostUiAsync<SoftwareDeploymentView>(client, "/api/software/deployments",
            new SoftwareDeploymentRequest(package.Id, SoftwareAction.Install, [deviceId], true));
        Assert.Equal("Running", deployment.State);
        var target = Assert.Single(deployment.Targets);

        var assignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.InstallSoftware, assignment.Kind);
        Assert.Contains("7zip.7zip", assignment.Argument, StringComparison.Ordinal);
        Assert.Contains("Winget", assignment.Argument, StringComparison.Ordinal);

        await ReportAsync(client, deviceId, key, assignment.Id, new SoftwareActionResult("Winget", "7zip.7zip", "install", 0, "Successfully installed"));

        var completed = await client.GetFromJsonAsync<SoftwareDeploymentView>($"/api/software/deployments/{deployment.Id:D}", JsonOptions);
        Assert.Equal("Completed", completed!.State);
        Assert.Equal(1, completed.SuccessCount);
        Assert.Equal("Completed", Assert.Single(completed.Targets).State);
        Assert.Equal(deviceId, target.DeviceId);
    }

    [Fact]
    public async Task FailedDeploymentIsReportedAsFailed()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "deploy.fail.owner");

        var package = await PostUiAsync<SoftwarePackageView>(client, "/api/software/packages",
            new SoftwarePackageRequest("Tool", SoftwareSource.Winget, "vendor.tool", "1.0", null, null));
        var deployment = await PostUiAsync<SoftwareDeploymentView>(client, "/api/software/deployments",
            new SoftwareDeploymentRequest(package.Id, SoftwareAction.Install, [deviceId], true));
        var assignment = await ClaimOperationAsync(client, deviceId, key);

        var payload = new OperationResultPayload(assignment.Id, OperationState.Failed, null, "installer exited with code 1603", DateTimeOffset.UtcNow);
        using var response = await client.PostAsJsonAsync("/api/agent/operation-result", AgentProtocol.Create(deviceId, key, payload), JsonOptions);
        response.EnsureSuccessStatusCode();

        var completed = await client.GetFromJsonAsync<SoftwareDeploymentView>($"/api/software/deployments/{deployment.Id:D}", JsonOptions);
        Assert.Equal("Failed", completed!.State);
        Assert.Equal(1, completed.FailureCount);
        Assert.Contains("1603", completed.Targets[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDeploymentCancelsQueuedOperations()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "deploy.cancel.owner");

        var package = await PostUiAsync<SoftwarePackageView>(client, "/api/software/packages",
            new SoftwarePackageRequest("Cancelable", SoftwareSource.Winget, "vendor.cancelable", null, null, null));
        var deployment = await PostUiAsync<SoftwareDeploymentView>(client, "/api/software/deployments",
            new SoftwareDeploymentRequest(package.Id, SoftwareAction.Install, [deviceId], true));

        using (var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/software/deployments/{deployment.Id:D}/cancel"))
        {
            cancel.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(cancel);
            response.EnsureSuccessStatusCode();
        }

        var cancelled = await client.GetFromJsonAsync<SoftwareDeploymentView>($"/api/software/deployments/{deployment.Id:D}", JsonOptions);
        Assert.Equal("Cancelled", cancelled!.State);
        Assert.Equal("Cancelled", cancelled.Targets[0].State);
    }

    [Fact]
    public async Task UnsignedMsiPackageIsRejected()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "deploy.validation.owner");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/software/packages")
        {
            Content = JsonContent.Create(new SoftwarePackageRequest("Unsigned", SoftwareSource.Msi, "https://example.invalid/tool.msi", null, null, null), options: JsonOptions)
        };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OperatorCannotDeploySoftware()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "deploy.operator.owner");
        await CreateUserAsync(factory, "deploy.operator", "Operator");

        using var operatorClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("deploy.operator", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await operatorClient.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/software/packages")
        {
            Content = JsonContent.Create(new SoftwarePackageRequest("Blocked", SoftwareSource.Winget, "vendor.blocked", null, null, null), options: JsonOptions)
        };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var forbidden = await operatorClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task WindowsUpdateScanAndInstallFlowIsRecorded()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "updates.owner");

        var scan = await PostUiAsync<OperationView>(client, $"/api/devices/{deviceId:D}/updates/scan", null);
        var assignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.ScanWindowsUpdates, assignment.Kind);

        var scanResult = new WindowsUpdateScanResult(DateTimeOffset.UtcNow,
        [
            new WindowsUpdateInfo("11111111-1111-1111-1111-111111111111", "Security Intelligence Update", "2267602", 4096, "Important", true)
        ], null);
        await ReportAsync(client, deviceId, key, assignment.Id, scanResult);

        var stored = await client.GetFromJsonAsync<WindowsUpdateScanResult>($"/api/devices/{deviceId:D}/updates", JsonOptions);
        Assert.Single(stored!.Updates);
        Assert.Equal("2267602", stored.Updates[0].KbArticle);
        Assert.NotNull(scan);

        var install = await PostUiAsync<OperationView>(client, $"/api/devices/{deviceId:D}/updates/install",
            new WindowsUpdateInstallArgument([stored.Updates[0].UpdateId]));
        var installAssignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.InstallWindowsUpdate, installAssignment.Kind);
        await ReportAsync(client, deviceId, key, installAssignment.Id, new WindowsUpdateInstallResult(1, 1, 0, true, string.Empty));

        var completed = await client.GetFromJsonAsync<OperationView>($"/api/operations/{install.Id:D}", JsonOptions);
        Assert.Equal(OperationState.Succeeded, completed!.State);
        Assert.Contains("rebootRequired", completed.ResultJson, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CreateUserAsync(EndToEndFactory factory, string username, string role)
    {
        var dbPath = Path.Combine(factory.DataDirectory, "controller.db");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO users(id, username, password_hash, role, created_at) VALUES ($id, $username, $hash, $role, $now);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$hash", PasswordHasher.Hash("a secure viewer password"));
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static async Task<(Guid DeviceId, ECDsa Key)> EnrollAsync(HttpClient client, string owner)
    {
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure deployment password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.5.0", true, "00:11:22:33:44:55");
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Deploy-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
    }

    private static async Task<OperationAssignment> ClaimOperationAsync(HttpClient client, Guid deviceId, ECDsa key)
    {
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.5.0", true, "00:11:22:33:44:55");
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 10, 1000, 16_000, 100, []);
        using var response = await client.PostAsJsonAsync("/api/agent/heartbeat",
            AgentProtocol.Create(deviceId, key, new HeartbeatPayload("Deploy-Node", inventory, telemetry)), JsonOptions);
        response.EnsureSuccessStatusCode();
        var heartbeat = (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions))!;
        return Assert.Single(heartbeat.Operations);
    }

    private static async Task ReportAsync<T>(HttpClient client, Guid deviceId, ECDsa key, Guid operationId, T result)
    {
        var payload = new OperationResultPayload(operationId, OperationState.Succeeded, JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);
        using var response = await client.PostAsJsonAsync("/api/agent/operation-result", AgentProtocol.Create(deviceId, key, payload), JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> PostUiAsync<T>(HttpClient client, string path, object? value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (value is not null) request.Content = JsonContent.Create(value, options: JsonOptions);
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }
}
