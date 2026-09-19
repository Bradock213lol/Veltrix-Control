using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class AdminWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task TerminalSessionLifecycleIsAuditedAndStreamed()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "terminal.owner", "00:11:22:33:44:55");

        var started = await PostUiAsync<TerminalStartResponse>(client, $"/api/devices/{deviceId:D}/terminal",
            new StartTerminalRequest(TerminalShell.CommandPrompt, "C:\\Windows"));
        Assert.Equal("Active", started.Session.State);

        var assignment = await ClaimOperationAsync(client, deviceId, key, started.Operation.Id);
        Assert.Equal(OperationKind.TerminalStart, assignment.Kind);
        await ReportAsync(client, deviceId, key, started.Operation.Id, new TerminalOutputResult(started.Session.Id, 31, "Microsoft Windows [Version 10.0]\r\nC:\\Windows>", false, null));

        var input = await PostUiAsync<TerminalOperationResponse>(client, $"/api/terminal/{started.Session.Id:D}/input", new TerminalInputRequest("echo check\r\n"));
        await ClaimOperationAsync(client, deviceId, key, input.Operation.Id);
        await ReportAsync(client, deviceId, key, input.Operation.Id, new TerminalOutputResult(started.Session.Id, 43, "check\r\n", false, null));
        var completed = await client.GetFromJsonAsync<OperationView>($"/api/operations/{input.Operation.Id:D}", JsonOptions);
        Assert.Equal(OperationState.Succeeded, completed!.State);
        Assert.Contains("check", completed.ResultJson, StringComparison.Ordinal);

        var output = await PostUiAsync<TerminalOperationResponse>(client, $"/api/terminal/{started.Session.Id:D}/output?since=31", null);
        await ClaimOperationAsync(client, deviceId, key, output.Operation.Id);
        await ReportAsync(client, deviceId, key, output.Operation.Id, new TerminalOutputResult(started.Session.Id, 43, "check\r\n", false, null));

        _ = await PostUiAsync<TerminalOperationResponse>(client, $"/api/terminal/{started.Session.Id:D}/stop", null);
        var sessions = await client.GetFromJsonAsync<TerminalSessionView[]>($"/api/devices/{deviceId:D}/terminal", JsonOptions);
        Assert.Equal("Closed", Assert.Single(sessions!).State);

        var audit = await client.GetFromJsonAsync<AuditEventView[]>("/api/audit?limit=200", JsonOptions);
        Assert.Contains(audit!, item => item.Action == "device.terminal.start");
        Assert.Contains(audit!, item => item.Action == "device.terminal.stop");
        Assert.Contains(audit!, item => item.Action == "device.terminal.input");
    }

    [Fact]
    public async Task InvalidTerminalRequestsAreRejected()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "invalid.owner", null);

        using var badShell = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/terminal")
        {
            Content = JsonContent.Create(new { Shell = "Bash", WorkingDirectory = "C:\\Windows" }, options: JsonOptions)
        };
        badShell.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var badShellResponse = await client.SendAsync(badShell);
        Assert.Equal(HttpStatusCode.BadRequest, badShellResponse.StatusCode);
    }

    [Fact]
    public async Task ViewerCannotUseTerminalOrWake()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "viewer.owner", "00:11:22:33:44:55");
        await CreateUserAsync(factory, "terminal.viewer", "Viewer");

        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("terminal.viewer", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await viewer.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }

        using (var terminal = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/terminal")
        {
            Content = JsonContent.Create(new StartTerminalRequest(TerminalShell.CommandPrompt, "C:\\Windows"), options: JsonOptions)
        })
        {
            terminal.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await viewer.SendAsync(terminal);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var wake = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/wake"))
        {
            wake.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await viewer.SendAsync(wake);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task WakeRequiresAReportedMacAddress()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "wake.owner", null);

        using var wake = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/wake");
        wake.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(wake);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WakeSendsMagicPacketWhenMacIsKnown()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "wake.mac.owner", "00:11:22:33:44:55");

        using var wake = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/wake");
        wake.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(wake);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = await client.GetFromJsonAsync<AuditEventView[]>("/api/audit?limit=200", JsonOptions);
        Assert.Contains(audit!, item => item.Action == "device.wake");
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

    private static async Task<(Guid DeviceId, ECDsa Key)> EnrollAsync(HttpClient client, string owner, string? mac)
    {
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure admin password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.4.0", true, mac);
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Admin-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
    }

    private static async Task<OperationAssignment> ClaimOperationAsync(HttpClient client, Guid deviceId, ECDsa key, Guid operationId)
    {
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.4.0", true, "00:11:22:33:44:55");
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 10, 1000, 16_000, 100, []);
        using var response = await client.PostAsJsonAsync("/api/agent/heartbeat",
            AgentProtocol.Create(deviceId, key, new HeartbeatPayload("Admin-Node", inventory, telemetry)), JsonOptions);
        response.EnsureSuccessStatusCode();
        var heartbeat = (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions))!;
        return Assert.Single(heartbeat.Operations, operation => operation.Id == operationId);
    }

    private static async Task ReportAsync(HttpClient client, Guid deviceId, ECDsa key, Guid operationId, TerminalOutputResult output)
    {
        var payload = new OperationResultPayload(operationId, OperationState.Succeeded, JsonSerializer.Serialize(output, JsonOptions), null, DateTimeOffset.UtcNow);
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
