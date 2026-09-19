using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class GameServerWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task GameServerLifecycleRunsThroughAgentOperations()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "gameserver.owner");

        var created = await PostUiAsync<JsonElement>(client, $"/api/devices/{deviceId:D}/gameservers",
            new GameServerRequest("Survival", "MinecraftJava", "servers/survival", 25565, 2048, 2, true, null, null));
        var serverId = created.GetProperty("server").GetProperty("id").GetGuid();

        var provision = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.GameServerProvision, provision.Kind);
        await ReportAsync(client, deviceId, key, provision.Id,
            new GameServerProvisionResult("MinecraftJava", "servers/survival", "1.21.1", @"C:\Java\bin\java.exe", "-jar server.jar nogui", 50_000_000, new string('A', 40)));

        var provisioned = await client.GetFromJsonAsync<GameServerView>($"/api/gameservers/{serverId:D}", JsonOptions);
        Assert.Equal("Stopped", provisioned!.State);
        Assert.Equal("1.21.1", provisioned.Version);

        using (var start = new HttpRequestMessage(HttpMethod.Post, $"/api/gameservers/{serverId:D}/start"))
        {
            start.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(start);
            response.EnsureSuccessStatusCode();
        }
        var startAssignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.GameServerStart, startAssignment.Kind);
        await ReportAsync(client, deviceId, key, startAssignment.Id,
            new GameServerStatusResult(serverId, true, false, null, 128, "Done (1.2s)! For help, type \"help\""));

        var running = await client.GetFromJsonAsync<GameServerView>($"/api/gameservers/{serverId:D}", JsonOptions);
        Assert.Equal("Running", running!.State);

        using (var console = new HttpRequestMessage(HttpMethod.Post, $"/api/gameservers/{serverId:D}/console")
        {
            Content = JsonContent.Create(new TerminalInputRequest("list"), options: JsonOptions)
        })
        {
            console.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(console);
            response.EnsureSuccessStatusCode();
        }
        var inputAssignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.GameServerInput, inputAssignment.Kind);
        await ReportAsync(client, deviceId, key, inputAssignment.Id,
            new GameServerStatusResult(serverId, true, false, null, 256, "There are 0 of a max of 20 players online."));

        using (var stop = new HttpRequestMessage(HttpMethod.Post, $"/api/gameservers/{serverId:D}/stop"))
        {
            stop.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(stop);
            response.EnsureSuccessStatusCode();
        }
        var stopAssignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.GameServerStop, stopAssignment.Kind);
        await ReportAsync(client, deviceId, key, stopAssignment.Id, new GameServerStatusResult(serverId, false, true, null, 300, "stop requested"));

        var stopped = await client.GetFromJsonAsync<GameServerView>($"/api/gameservers/{serverId:D}", JsonOptions);
        Assert.Equal("Stopped", stopped!.State);

        var events = await client.GetFromJsonAsync<GameServerEventView[]>($"/api/gameservers/{serverId:D}/events", JsonOptions);
        Assert.Contains(events!, item => item.Kind == "provisioned");
        Assert.Contains(events!, item => item.Kind == "started");
        Assert.Contains(events!, item => item.Kind == "stopped");
    }

    [Fact]
    public async Task CrashedServerRestartsAutomaticallyWhenEnabled()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "gameserver.crash.owner");

        var created = await PostUiAsync<JsonElement>(client, $"/api/devices/{deviceId:D}/gameservers",
            new GameServerRequest("Crashy", "MinecraftJava", "servers/crashy", 25565, 2048, 2, true, null, null));
        var serverId = created.GetProperty("server").GetProperty("id").GetGuid();
        var provision = await ClaimOperationAsync(client, deviceId, key);
        await ReportAsync(client, deviceId, key, provision.Id,
            new GameServerProvisionResult("MinecraftJava", "servers/crashy", "1.21.1", "java.exe", "-jar server.jar", 1, new string('A', 40)));

        using (var start = new HttpRequestMessage(HttpMethod.Post, $"/api/gameservers/{serverId:D}/start"))
        {
            start.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(start);
            response.EnsureSuccessStatusCode();
        }
        var startAssignment = await ClaimOperationAsync(client, deviceId, key);
        await ReportAsync(client, deviceId, key, startAssignment.Id,
            new GameServerStatusResult(serverId, false, true, 1, 64, "Exception in server tick loop"));

        var state = await WaitForStateAsync(client, serverId, "Starting", TimeSpan.FromSeconds(15));
        Assert.Equal("Starting", state.State);

        var restartAssignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.GameServerStart, restartAssignment.Kind);

        var events = await client.GetFromJsonAsync<GameServerEventView[]>($"/api/gameservers/{serverId:D}/events", JsonOptions);
        Assert.Contains(events!, item => item.Kind == "crashed");
        Assert.Contains(events!, item => item.Kind == "restarting");
    }

    [Fact]
    public async Task InvalidRequestsAndPermissionsAreEnforced()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "gameserver.validation.owner");
        await CreateUserAsync(factory, "gameserver.viewer", "Viewer");

        using var invalid = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/gameservers")
        {
            Content = JsonContent.Create(new GameServerRequest("Bad port", "MinecraftJava", "servers/bad", 70_000, 2048, 2, false, null, null), options: JsonOptions)
        };
        invalid.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var unknownAdapter = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/gameservers")
        {
            Content = JsonContent.Create(new GameServerRequest("Unknown", "Terraria", "servers/unknown", 7777, 2048, 2, false, null, null), options: JsonOptions)
        };
        unknownAdapter.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var unknownResponse = await client.SendAsync(unknownAdapter);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);

        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("gameserver.viewer", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await viewer.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/gameservers")).StatusCode);
    }

    private static async Task<GameServerView> WaitForStateAsync(HttpClient client, Guid serverId, string state, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        GameServerView? current = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            current = await client.GetFromJsonAsync<GameServerView>($"/api/gameservers/{serverId:D}", JsonOptions);
            if (current?.State == state) return current;
            await Task.Delay(500);
        }
        return current ?? throw new TimeoutException("The server never appeared.");
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
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure gameserver password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.8.0", true, "00:11:22:33:44:55");
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Game-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
    }

    private static async Task<OperationAssignment> ClaimOperationAsync(HttpClient client, Guid deviceId, ECDsa key)
    {
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.8.0", true, "00:11:22:33:44:55");
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 10, 1000, 16_000, 100, []);
        using var response = await client.PostAsJsonAsync("/api/agent/heartbeat",
            AgentProtocol.Create(deviceId, key, new HeartbeatPayload("Game-Node", inventory, telemetry)), JsonOptions);
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
