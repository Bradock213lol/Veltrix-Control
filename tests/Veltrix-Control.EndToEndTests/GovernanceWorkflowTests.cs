using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Notifications;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class GovernanceWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task DeviceTagsAndFavoritesRoundTrip()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, _) = await EnrollAsync(client, "governance.owner");

        var tagged = await PostUiAsync<DeviceTagView[]>(client, $"/api/devices/{deviceId:D}/tags", new DeviceTagRequest("Production"));
        Assert.Contains(tagged, tag => tag.Tag == "Production");

        var devices = await client.GetFromJsonAsync<DeviceSummary[]>("/api/devices", JsonOptions);
        Assert.Contains("Production", Assert.Single(devices!).Tags!);

        var invalid = await PostAsync(client, $"/api/devices/{deviceId:D}/tags", new DeviceTagRequest("bad/tag"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using (var favorite = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/favorite?favorite=true"))
        {
            favorite.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(favorite);
            response.EnsureSuccessStatusCode();
        }
        var favorited = await client.GetFromJsonAsync<DeviceSummary[]>("/api/devices", JsonOptions);
        Assert.True(Assert.Single(favorited!).IsFavorite);

        using (var remove = new HttpRequestMessage(HttpMethod.Delete, $"/api/devices/{deviceId:D}/tags/Production"))
        {
            remove.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(remove);
            response.EnsureSuccessStatusCode();
        }
        Assert.Empty(await client.GetFromJsonAsync<DeviceTagView[]>($"/api/devices/{deviceId:D}/tags", JsonOptions) ?? []);
    }

    [Fact]
    public async Task NotificationSettingsRequireHttpsAndAdmin()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await EnrollAsync(client, "notify.owner");

        using (var insecure = new HttpRequestMessage(HttpMethod.Put, "/api/settings/notifications")
        {
            Content = JsonContent.Create(new NotificationSettingsRequest(true, "http://hooks.example.com/veltrix", null), options: JsonOptions)
        })
        {
            insecure.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(insecure);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var save = new HttpRequestMessage(HttpMethod.Put, "/api/settings/notifications")
        {
            Content = JsonContent.Create(new NotificationSettingsRequest(true, "https://hooks.example.com/veltrix", "top-secret"), options: JsonOptions)
        })
        {
            save.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(save);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<NotificationSettingsView>(JsonOptions);
            Assert.True(saved!.Enabled);
            Assert.True(saved.HasSecret);
        }

        var loaded = await client.GetFromJsonAsync<NotificationSettingsView>("/api/settings/notifications", JsonOptions);
        Assert.Equal("https://hooks.example.com/veltrix", loaded!.WebhookUrl);

        await CreateUserAsync(factory, "notify.viewer", "Viewer");
        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("notify.viewer", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await viewer.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/settings/notifications")).StatusCode);
    }

    [Fact]
    public void SignatureHelperMatchesKnownVector()
    {
        var signature = NotificationSignature.Compute("key", "The quick brown fox jumps over the lazy dog");
        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", signature);
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
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure governance password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.10.8", true, "00:11:22:33:44:55");
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Governance-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value, options: JsonOptions) };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        return await client.SendAsync(request);
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