using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexaGrid.Core.Security;
using NexaGrid.Contracts;

var arguments = ParseArguments(args);
if (!arguments.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Usage: NexaGrid.Simulator --token <enrollment-code> [--controller http://localhost:5187] [--name Simulated-PC-01]");
    return 2;
}

var controller = arguments.GetValueOrDefault("controller", "http://localhost:5187");
var name = arguments.GetValueOrDefault("name", "Simulated-PC-01");
using var http = new HttpClient { BaseAddress = new Uri(controller.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
var inventory = new HardwareInventory(
    "NexaGrid Node Simulator",
    Environment.OSVersion.VersionString,
    RuntimeInformation.OSArchitecture.ToString(),
    12,
    32L * 1024 * 1024 * 1024,
    [new DiskInventory("SIM:\\", "NTFS", 512L * 1024 * 1024 * 1024, 371L * 1024 * 1024 * 1024)],
    "0.1.0",
    true);
var enrollmentRequest = new EnrollmentRequest(token, name, AgentProtocol.ExportPublicKey(key), inventory);
using var enrollmentResponse = await http.PostAsJsonAsync("api/agent/enroll", enrollmentRequest, json);
if (!enrollmentResponse.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Enrollment failed: {(int)enrollmentResponse.StatusCode} {await enrollmentResponse.Content.ReadAsStringAsync()}");
    return 1;
}
var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<EnrollmentResponse>(json)
    ?? throw new InvalidOperationException("Empty enrollment response.");
Console.WriteLine($"{name} enrolled as {enrollment.DeviceId:D}. Press Ctrl+C to stop.");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
var random = new Random(name.GetHashCode(StringComparison.Ordinal));
while (!stopping.IsCancellationRequested)
{
    try
    {
        var cpu = Math.Clamp(18 + Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 8d) * 12 + random.NextDouble() * 5, 1, 100);
        var usedMemory = (long)((9.5 + random.NextDouble() * 1.5) * 1024 * 1024 * 1024);
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, cpu, usedMemory, inventory.TotalMemoryBytes, 86400 + Environment.TickCount64 / 1000, inventory.Disks);
        var signed = AgentProtocol.Create(enrollment.DeviceId, key, new HeartbeatPayload(name, inventory, telemetry));
        using var heartbeatResponse = await http.PostAsJsonAsync("api/agent/heartbeat", signed, json, stopping.Token);
        heartbeatResponse.EnsureSuccessStatusCode();
        var heartbeat = await heartbeatResponse.Content.ReadFromJsonAsync<HeartbeatResponse>(json, stopping.Token)
            ?? throw new InvalidOperationException("Empty heartbeat response.");
        foreach (var operation in heartbeat.Operations)
        {
            Console.WriteLine($"Simulating {operation.Kind} ({operation.Id:D})");
            var resultJson = operation.Kind == OperationKind.ListDirectory
                ? JsonSerializer.Serialize(new[]
                {
                    new FileEntry("Documents", "Documents", true, null, DateTimeOffset.UtcNow.AddDays(-2)),
                    new FileEntry("status.txt", "status.txt", false, 2048, DateTimeOffset.UtcNow)
                }, json)
                : JsonSerializer.Serialize(new { simulated = true }, json);
            var result = new OperationResultPayload(operation.Id, OperationState.Succeeded, resultJson, null, DateTimeOffset.UtcNow);
            using var resultResponse = await http.PostAsJsonAsync("api/agent/operation-result", AgentProtocol.Create(enrollment.DeviceId, key, result), json, stopping.Token);
            resultResponse.EnsureSuccessStatusCode();
        }
        await Task.Delay(TimeSpan.FromSeconds(heartbeat.NextHeartbeatSeconds), stopping.Token);
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        break;
    }
    catch (HttpRequestException exception)
    {
        Console.Error.WriteLine($"Controller unavailable: {exception.Message}. Retrying...");
        await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
    }
}
return 0;

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index + 1 < values.Length; index += 2)
    {
        if (values[index].StartsWith("--", StringComparison.Ordinal)) parsed[values[index][2..]] = values[index + 1];
    }
    return parsed;
}
