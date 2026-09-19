using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class IntegrationWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task IntegrationLifecycleRedactsCredentialsAndReportsHealth()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "integration.owner");

        var integration = await PostUiAsync<IntegrationView>(client, "/api/integrations",
            new IntegrationRequest("Docker", "Local engine", "npipe://./pipe/docker_engine_definitely_missing", "some-credential", true));
        Assert.True(integration.HasCredential);
        Assert.Null(integration.HealthState);

        var listed = await client.GetFromJsonAsync<IntegrationView[]>("/api/integrations", JsonOptions);
        var single = Assert.Single(listed!);
        Assert.DoesNotContain("some-credential", JsonSerializer.Serialize(single, JsonOptions), StringComparison.Ordinal);

        using (var health = new HttpRequestMessage(HttpMethod.Post, $"/api/integrations/{integration.Id:D}/health"))
        {
            health.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(health);
            response.EnsureSuccessStatusCode();
            var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("Unavailable", payload.GetProperty("state").GetString());
        }

        var refreshed = await client.GetFromJsonAsync<IntegrationView>($"/api/integrations/{integration.Id:D}", JsonOptions);
        Assert.Equal("Unavailable", refreshed!.HealthState);
        Assert.NotNull(refreshed.HealthDetail);

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/integrations/{integration.Id:D}"))
        {
            delete.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(delete);
            response.EnsureSuccessStatusCode();
        }
        Assert.Empty((await client.GetFromJsonAsync<IntegrationView[]>("/api/integrations", JsonOptions))!);
    }

    [Fact]
    public async Task UnsafeEndpointsAndKindsAreRejected()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "integration.validation.owner");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/integrations",
            new IntegrationRequest("Pterodactyl", "Bad", "http://panel.example.com", null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/integrations",
            new IntegrationRequest("Terraria", "Unknown", "https://panel.example.com", null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/integrations",
            new IntegrationRequest("Docker", "Remote plaintext", "http://192.168.1.50:2375", null, true))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/integrations",
            new IntegrationRequest("Docker", "Loopback", "tcp://127.0.0.1:2375", null, true))).StatusCode);
    }

    [Fact]
    public async Task OperatorCannotManageIntegrations()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "integration.operator.owner");
        await CreateUserAsync(factory, "integration.operator", "Operator");

        using var operatorClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("integration.operator", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await operatorClient.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.GetAsync("/api/integrations")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations")
        {
            Content = JsonContent.Create(new IntegrationRequest("Docker", "Blocked", "npipe://./pipe/docker_engine", null, true), options: JsonOptions)
        };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ActionsRequireConfirmation()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "integration.action.owner");
        var integration = await PostUiAsync<IntegrationView>(client, "/api/integrations",
            new IntegrationRequest("Docker", "Engine", "npipe://./pipe/docker_engine", null, true));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integrations/{integration.Id:D}/actions")
        {
            Content = JsonContent.Create(new IntegrationActionRequest("restart", "container:0123456789ab", false), options: JsonOptions)
        };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value, options: JsonOptions) };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        return await client.SendAsync(request);
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
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure integration password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.9.0", true, "00:11:22:33:44:55");
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Integration-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
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
