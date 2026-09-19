using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class FleetWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ControllerAgentOperationResultWorkflowCompletes()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        (await client.PostAsJsonAsync("/api/setup", new SetupRequest("e2e.owner", "a secure end to end password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));

        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 12, 32_000, [], "0.2.0", true);
        var enrollmentResponse = await client.PostAsJsonAsync("/api/agent/enroll", new EnrollmentRequest(token.Code, "E2E-Node", AgentProtocol.ExportPublicKey(deviceKey), inventory));
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = (await enrollmentResponse.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;

        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 32, 10_000, 32_000, 7200, []);
        var firstHeartbeat = await PostAgentAsync<HeartbeatResponse>(client, "/api/agent/heartbeat", AgentProtocol.Create(enrollment.DeviceId, deviceKey, new HeartbeatPayload("E2E-Node", inventory, telemetry)));
        Assert.Empty(firstHeartbeat.Operations);

        var devices = await client.GetFromJsonAsync<DeviceSummary[]>("/api/devices", JsonOptions);
        var device = Assert.Single(devices!);
        Assert.True(device.Online);
        Assert.Equal(32, device.Telemetry!.CpuPercent);

        var operation = await PostUiAsync<OperationView>(client, $"/api/devices/{enrollment.DeviceId:D}/operations", new OperationRequest(OperationKind.ListDirectory, string.Empty, true));
        var secondHeartbeat = await PostAgentAsync<HeartbeatResponse>(client, "/api/agent/heartbeat", AgentProtocol.Create(enrollment.DeviceId, deviceKey, new HeartbeatPayload("E2E-Node", inventory, telemetry)));
        var assignment = Assert.Single(secondHeartbeat.Operations);
        Assert.Equal(operation.Id, assignment.Id);

        var files = JsonSerializer.Serialize(new[] { new FileEntry("status.txt", "status.txt", false, 42, DateTimeOffset.UtcNow) }, JsonOptions);
        var result = new OperationResultPayload(operation.Id, OperationState.Succeeded, files, null, DateTimeOffset.UtcNow);
        using var resultResponse = await client.PostAsJsonAsync("/api/agent/operation-result", AgentProtocol.Create(enrollment.DeviceId, deviceKey, result), JsonOptions);
        resultResponse.EnsureSuccessStatusCode();

        var completed = await client.GetFromJsonAsync<OperationView>($"/api/operations/{operation.Id:D}", JsonOptions);
        Assert.Equal(OperationState.Succeeded, completed!.State);
        Assert.Contains("status.txt", completed.ResultJson, StringComparison.Ordinal);
        var integrity = await client.GetFromJsonAsync<AuditIntegrity>("/api/audit/integrity", JsonOptions);
        Assert.True(integrity!.Valid);
    }

    private static async Task<T> PostUiAsync<T>(HttpClient client, string path, object value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value, options: JsonOptions) };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private static async Task<T> PostAgentAsync<T>(HttpClient client, string path, SignedAgentMessage message)
    {
        using var response = await client.PostAsJsonAsync(path, message, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private sealed record AuditIntegrity(bool Valid);
}

public sealed class EndToEndFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "Veltrix-Control.E2E", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Controller:DataDirectory"] = DataDirectory,
                ["Controller:EnableHttpsListener"] = "false",
                ["Controller:EnableLocalHttpListener"] = "false"
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
        }
    }
}
