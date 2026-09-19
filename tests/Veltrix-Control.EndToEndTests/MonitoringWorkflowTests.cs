using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class MonitoringWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task BackupLifecycleCompletesAndIsListed()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "backup.owner");

        var response = await PostUiAsync<JsonElement>(client, $"/api/devices/{deviceId:D}/backups",
            new BackupRequest("Nightly data", "data", "backups/data-001.zip", 30));
        var backupId = response.GetProperty("backup").GetProperty("id").GetGuid();

        var assignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.CreateBackup, assignment.Kind);
        await ReportAsync(client, deviceId, key, assignment.Id,
            new BackupOperationResult("backups/data-001.zip", 2048, new string('A', 64), 12));

        var backups = await client.GetFromJsonAsync<BackupView[]>($"/api/devices/{deviceId:D}/backups", JsonOptions);
        var backup = Assert.Single(backups!);
        Assert.Equal("Completed", backup.State);
        Assert.Equal(2048, backup.SizeBytes);
        Assert.Equal(backupId, backup.Id);

        using var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/backups/{backupId:D}/restore")
        {
            Content = JsonContent.Create(new RestoreBackupArgument("ignored-by-server", "restored"), options: JsonOptions)
        };
        restore.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var restoreResponse = await client.SendAsync(restore);
        Assert.True(restoreResponse.IsSuccessStatusCode, await restoreResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FailedBackupIsReportedAsFailed()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "backup.fail.owner");

        var response = await PostUiAsync<JsonElement>(client, $"/api/devices/{deviceId:D}/backups",
            new BackupRequest("Failing", "data", "backups/fail.zip", 7));
        var backupId = response.GetProperty("backup").GetProperty("id").GetGuid();
        var assignment = await ClaimOperationAsync(client, deviceId, key);

        var payload = new OperationResultPayload(assignment.Id, OperationState.Failed, null, "The backup source does not exist.", DateTimeOffset.UtcNow);
        using var result = await client.PostAsJsonAsync("/api/agent/operation-result", AgentProtocol.Create(deviceId, key, payload), JsonOptions);
        result.EnsureSuccessStatusCode();

        var backups = await client.GetFromJsonAsync<BackupView[]>($"/api/devices/{deviceId:D}/backups", JsonOptions);
        Assert.Equal("Failed", Assert.Single(backups!).State);
        Assert.Contains("source", backups![0].Error, StringComparison.OrdinalIgnoreCase);
        _ = backupId;
    }

    [Fact]
    public async Task AlertsCanBeListedAndAcknowledged()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "alert.owner");
        var alertId = await InsertAlertAsync(factory);

        var alerts = await client.GetFromJsonAsync<AlertView[]>("/api/alerts?state=Open", JsonOptions);
        Assert.Contains(alerts!, alert => alert.Id == alertId);

        using (var acknowledge = new HttpRequestMessage(HttpMethod.Post, $"/api/alerts/{alertId:D}/acknowledge"))
        {
            acknowledge.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(acknowledge);
            response.EnsureSuccessStatusCode();
        }

        var updated = await client.GetFromJsonAsync<AlertView[]>("/api/alerts", JsonOptions);
        var alert = updated!.First(item => item.Id == alertId);
        Assert.Equal("Acknowledged", alert.State);
        Assert.NotNull(alert.AcknowledgedBy);
    }

    [Fact]
    public async Task AutomationsSupportLifecycleAndValidation()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "automation.owner");

        var automation = await PostUiAsync<AutomationView>(client, "/api/automations",
            new AutomationRequest("Nightly scan", true,
                new AutomationTriggerRequest("Schedule", null, null, 60),
                null,
                new AutomationActionRequest("QueueOperation", null, null, "ScanWindowsUpdates"),
                300));
        Assert.True(automation.Enabled);

        var disabled = await PostUiAsync<object?>(client, $"/api/automations/{automation.Id:D}/disable", null);
        _ = disabled;
        var listed = await client.GetFromJsonAsync<AutomationView[]>("/api/automations", JsonOptions);
        Assert.False(listed!.Single(item => item.Id == automation.Id).Enabled);

        using (var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/automations")
        {
            Content = JsonContent.Create(new AutomationRequest("Unsafe", true,
                new AutomationTriggerRequest("AlertRaised", null, null, null),
                null,
                new AutomationActionRequest("QueueOperation", null, null, "CreateFile"),
                300), options: JsonOptions)
        })
        {
            invalid.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/automations/{automation.Id:D}"))
        {
            delete.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(delete);
            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ViewerCannotAccessBackupsOrAutomations()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "monitor.viewer.owner");
        await CreateUserAsync(factory, "monitor.viewer", "Viewer");

        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("monitor.viewer", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await viewer.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/devices/{deviceId:D}/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/automations")).StatusCode);
    }

    private static async Task<Guid> InsertAlertAsync(EndToEndFactory factory)
    {
        var alertId = Guid.NewGuid();
        var dbPath = Path.Combine(factory.DataDirectory, "controller.db");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO alerts(id, device_id, severity, code, title, message, state, created_at) VALUES ($id, NULL, 'Warning', 'device.disk', 'Low disk space', 'Test alert', 'Open', $now);";
        command.Parameters.AddWithValue("$id", alertId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return alertId;
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
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure monitoring password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.6.0", true, "00:11:22:33:44:55");
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Monitor-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
    }

    private static async Task<OperationAssignment> ClaimOperationAsync(HttpClient client, Guid deviceId, ECDsa key)
    {
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.6.0", true, "00:11:22:33:44:55");
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 10, 1000, 16_000, 100, []);
        using var response = await client.PostAsJsonAsync("/api/agent/heartbeat",
            AgentProtocol.Create(deviceId, key, new HeartbeatPayload("Monitor-Node", inventory, telemetry)), JsonOptions);
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
        if (typeof(T) == typeof(object)) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }
}
