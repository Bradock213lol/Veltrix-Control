using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class ComputeWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SchedulerAssignsAJobToAnEligibleDevice()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "compute.owner");
        await HeartbeatAsync(client, deviceId, key);

        var job = await PostUiAsync<ComputeJobView>(client, "/api/compute/jobs", new ComputeJobRequest(
            "Fixture job", 100,
            new ComputeRequirements(1, 1024 * 1024, 1024 * 1024, false, null, deviceId),
            new ComputeCommand(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 1 127.0.0.1", null),
            120, 1));
        Assert.Equal("Queued", job.State);

        var assigned = await WaitForJobAsync(client, job.Id, TimeSpan.FromSeconds(40), "Running");
        Assert.Equal(deviceId, assigned.AssignedDeviceId);

        var assignment = await ClaimOperationAsync(client, deviceId, key);
        Assert.Equal(OperationKind.RunComputeJob, assignment.Kind);
        Assert.Contains("ping.exe", assignment.Argument, StringComparison.OrdinalIgnoreCase);

        var result = new ComputeJobResult(job.Id, 0, "reply from 127.0.0.1", false);
        var payload = new OperationResultPayload(assignment.Id, OperationState.Succeeded, JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);
        using var response = await client.PostAsJsonAsync("/api/agent/operation-result", AgentProtocol.Create(deviceId, key, payload), JsonOptions);
        response.EnsureSuccessStatusCode();

        var completed = await WaitForJobAsync(client, job.Id, TimeSpan.FromSeconds(20), "Succeeded");
        Assert.Contains("127.0.0.1", completed.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GpuJobsFailWithAClearMessage()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "compute.gpu.owner");
        await HeartbeatAsync(client, deviceId, key);

        var job = await PostUiAsync<ComputeJobView>(client, "/api/compute/jobs", new ComputeJobRequest(
            "GPU job", 10,
            new ComputeRequirements(1, 1024 * 1024, 0, true, null, deviceId),
            new ComputeCommand(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 1 127.0.0.1", null),
            60, 1));

        var failed = await WaitForJobAsync(client, job.Id, TimeSpan.FromSeconds(40), "Failed");
        Assert.Contains("GPU", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputePolicyRejectsJobsInGamingMode()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, key) = await EnrollAsync(client, "compute.gaming.owner");
        await HeartbeatAsync(client, deviceId, key);

        var policy = await PutUiAsync<ComputePolicyView>(client, $"/api/devices/{deviceId:D}/compute-policy",
            new ComputePolicyRequest("Gaming", 0, 0, 0));
        Assert.Equal("Gaming", policy.Mode);

        var job = await PostUiAsync<ComputeJobView>(client, "/api/compute/jobs", new ComputeJobRequest(
            "Blocked job", 50,
            new ComputeRequirements(1, 1024 * 1024, 0, false, null, deviceId),
            new ComputeCommand(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 1 127.0.0.1", null),
            60, 1));

        await Task.Delay(TimeSpan.FromSeconds(14));
        var current = await client.GetFromJsonAsync<ComputeJobView>($"/api/compute/jobs/{job.Id:D}", JsonOptions);
        Assert.Equal("Queued", current!.State);
    }

    [Fact]
    public async Task QueuedJobsCanBeCancelledAndPermissionsAreEnforced()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "compute.cancel.owner");

        var job = await PostUiAsync<ComputeJobView>(client, "/api/compute/jobs", new ComputeJobRequest(
            "Cancel me", 1, new ComputeRequirements(1, 0, 0, false, null, null),
            new ComputeCommand(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1", null), 600, 1));

        using (var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/compute/jobs/{job.Id:D}/cancel"))
        {
            cancel.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(cancel);
            response.EnsureSuccessStatusCode();
        }

        var cancelled = await client.GetFromJsonAsync<ComputeJobView>($"/api/compute/jobs/{job.Id:D}", JsonOptions);
        Assert.Equal("Cancelled", cancelled!.State);

        var invalid = await client.PostAsJsonAsync("/api/compute/jobs", new ComputeJobRequest(
            "Bad", 1, new ComputeRequirements(1, 0, 0, false, null, null),
            new ComputeCommand("relative.exe", null, null), 60, 1), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static async Task<ComputeJobView> WaitForJobAsync(HttpClient client, Guid jobId, TimeSpan timeout, string state)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ComputeJobView? current = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            current = await client.GetFromJsonAsync<ComputeJobView>($"/api/compute/jobs/{jobId:D}", JsonOptions);
            if (current?.State == state) return current;
            if (current?.State is "Failed" or "Cancelled" or "Succeeded") return current;
            await Task.Delay(2000);
        }
        return current ?? throw new TimeoutException("The job never appeared.");
    }

    private static async Task<(Guid DeviceId, ECDsa Key)> EnrollAsync(HttpClient client, string owner)
    {
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure compute password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000_000_000, [new DiskInventory("C:\\", "NTFS", 500_000_000_000, 250_000_000_000)], "0.7.0", true, "00:11:22:33:44:55");
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Compute-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
    }

    private static async Task HeartbeatAsync(HttpClient client, Guid deviceId, ECDsa key)
    {
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000_000_000, [new DiskInventory("C:\\", "NTFS", 500_000_000_000, 250_000_000_000)], "0.7.0", true, "00:11:22:33:44:55");
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 5, 2_000_000_000, 16_000_000_000, 100, [new DiskInventory("C:\\", "NTFS", 500_000_000_000, 250_000_000_000)]);
        using var response = await client.PostAsJsonAsync("/api/agent/heartbeat",
            AgentProtocol.Create(deviceId, key, new HeartbeatPayload("Compute-Node", inventory, telemetry)), JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<OperationAssignment> ClaimOperationAsync(HttpClient client, Guid deviceId, ECDsa key)
    {
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000_000_000, [new DiskInventory("C:\\", "NTFS", 500_000_000_000, 250_000_000_000)], "0.7.0", true, "00:11:22:33:44:55");
        var telemetry = new TelemetrySnapshot(DateTimeOffset.UtcNow, 5, 2_000_000_000, 16_000_000_000, 100, [new DiskInventory("C:\\", "NTFS", 500_000_000_000, 250_000_000_000)]);
        using var response = await client.PostAsJsonAsync("/api/agent/heartbeat",
            AgentProtocol.Create(deviceId, key, new HeartbeatPayload("Compute-Node", inventory, telemetry)), JsonOptions);
        response.EnsureSuccessStatusCode();
        var heartbeat = (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions))!;
        return Assert.Single(heartbeat.Operations);
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

    private static async Task<T> PutUiAsync<T>(HttpClient client, string path, object value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(value, options: JsonOptions) };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }
}
