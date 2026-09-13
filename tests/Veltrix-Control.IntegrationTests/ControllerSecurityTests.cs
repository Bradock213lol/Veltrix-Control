using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.IntegrationTests;

public sealed class ControllerSecurityTests
{
    [Fact]
    public async Task AnonymousManagementRequestIsRejected()
    {
        using var factory = new ControllerFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedWriteRequiresVerificationHeader()
    {
        using var factory = new ControllerFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);
        using var response = await client.PostAsJsonAsync("/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EnrollmentCodeIsSingleUse()
    {
        using var factory = new ControllerFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);
        var token = await CreateTokenAsync(client);
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = await client.PostAsJsonAsync("/api/agent/enroll", Enrollment(token.Code, "Node-1", firstKey));
        var second = await client.PostAsJsonAsync("/api/agent/enroll", Enrollment(token.Code, "Node-2", secondKey));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task SignedHeartbeatCannotBeReplayed()
    {
        using var factory = new ControllerFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);
        var token = await CreateTokenAsync(client);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrolledResponse = await client.PostAsJsonAsync("/api/agent/enroll", Enrollment(token.Code, "Node-1", key));
        var enrolled = await enrolledResponse.Content.ReadFromJsonAsync<EnrollmentResponse>();
        var signed = AgentProtocol.Create(enrolled!.DeviceId, key, Heartbeat());
        var first = await client.PostAsJsonAsync("/api/agent/heartbeat", signed);
        var replay = await client.PostAsJsonAsync("/api/agent/heartbeat", signed);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task RevokedEnrollmentCodeCannotBeUsed()
    {
        using var factory = new ControllerFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);
        var token = await CreateTokenAsync(client);
        using var revoke = new HttpRequestMessage(HttpMethod.Post, $"/api/enrollment/tokens/{token.Id:D}/revoke");
        revoke.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var revoked = await client.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var enrollment = await client.PostAsJsonAsync("/api/agent/enroll", Enrollment(token.Code, "Revoked-Node", key));
        Assert.Equal(HttpStatusCode.Unauthorized, enrollment.StatusCode);
    }

    [Fact]
    public async Task LocalInstallerBootstrapCodeIsImportedOnce()
    {
        using var factory = new ControllerFactory();
        Directory.CreateDirectory(factory.DataDirectory);
        var code = SecretGenerator.CreateEnrollmentCode();
        var codePath = Path.Combine(factory.DataDirectory, VeltrixControl.Controller.Services.CombinedRoleBootstrap.BootstrapFileName);
        await File.WriteAllTextAsync(codePath, code);
        using var client = factory.CreateClient();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var enrollment = await client.PostAsJsonAsync("/api/agent/enroll", Enrollment(code, "Combined-Node", key));
        Assert.Equal(HttpStatusCode.OK, enrollment.StatusCode);
        Assert.False(File.Exists(codePath));
    }

    internal static async Task SetupAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/setup", new SetupRequest("test.owner", "correct horse battery staple"));
        response.EnsureSuccessStatusCode();
    }

    internal static async Task<EnrollmentTokenResponse> CreateTokenAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/enrollment/tokens")
        {
            Content = JsonContent.Create(new CreateEnrollmentTokenRequest(15))
        };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentTokenResponse>())!;
    }

    internal static EnrollmentRequest Enrollment(string code, string name, ECDsa key) =>
        new(code, name, AgentProtocol.ExportPublicKey(key), Inventory());

    internal static HardwareInventory Inventory() => new("Windows Test", "10.0", "X64", 8, 16_000, [], "0.1.1", true);

    internal static HeartbeatPayload Heartbeat() => new(
        "Node-1",
        Inventory(),
        new TelemetrySnapshot(DateTimeOffset.UtcNow, 25, 8_000, 16_000, 3600, []));
}
