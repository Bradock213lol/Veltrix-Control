using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class UserAdministrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task UsersCanBeCreatedRoleChangedPasswordResetAndDeleted()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);

        var created = await PostUiAsync<UserView>(client, "/api/users", new CreateUserRequest("operator.one", "a secure operator password", "Operator"));
        Assert.Equal("Operator", created.Role);

        var duplicate = await PostAsync(client, "/api/users", new CreateUserRequest("operator.one", "a secure operator password", "Viewer"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using (var role = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{created.Id:D}/role")
        {
            Content = JsonContent.Create(new UpdateUserRoleRequest("Viewer"), options: JsonOptions)
        })
        {
            role.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(role);
            response.EnsureSuccessStatusCode();
        }

        using (var password = new HttpRequestMessage(HttpMethod.Post, $"/api/users/{created.Id:D}/password")
        {
            Content = JsonContent.Create(new ResetPasswordRequest("a rotated operator password"), options: JsonOptions)
        })
        {
            password.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(password);
            response.EnsureSuccessStatusCode();
        }

        var users = await client.GetFromJsonAsync<UserView[]>("/api/users", JsonOptions);
        Assert.Contains(users!, user => user.Username == "operator.one" && user.Role == "Viewer");

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{created.Id:D}"))
        {
            delete.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(delete);
            response.EnsureSuccessStatusCode();
        }
        Assert.DoesNotContain((await client.GetFromJsonAsync<UserView[]>("/api/users", JsonOptions))!, user => user.Username == "operator.one");
    }

    [Fact]
    public async Task LastOwnerCannotBeDemotedOrDeleted()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);
        var owner = Assert.Single((await client.GetFromJsonAsync<UserView[]>("/api/users", JsonOptions))!, user => user.Role == "Owner");

        using (var role = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{owner.Id:D}/role")
        {
            Content = JsonContent.Create(new UpdateUserRoleRequest("Administrator"), options: JsonOptions)
        })
        {
            role.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(role);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{owner.Id:D}"))
        {
            delete.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(delete);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Fact]
    public async Task InvalidUsersAndPermissionsAreRejected()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);
        await CreateUserAsync(factory, "admin.viewer", "Viewer");

        using var invalidRole = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = JsonContent.Create(new CreateUserRequest("bad.role", "a secure operator password", "SuperUser"), options: JsonOptions)
        };
        invalidRole.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var invalidRoleResponse = await client.SendAsync(invalidRole);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRoleResponse.StatusCode);

        using var shortPassword = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = JsonContent.Create(new CreateUserRequest("short.password", "short", "Viewer"), options: JsonOptions)
        };
        shortPassword.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var shortPasswordResponse = await client.SendAsync(shortPassword);
        Assert.Equal(HttpStatusCode.BadRequest, shortPasswordResponse.StatusCode);

        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("admin.viewer", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await viewer.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/system/info")).StatusCode);
    }

    [Fact]
    public async Task SystemInfoReportsCountsForAdministrators()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SetupAsync(client);

        var info = await client.GetFromJsonAsync<JsonElement>("/api/system/info", JsonOptions);
        Assert.True(info.GetProperty("version").GetString()!.Length > 0);
        Assert.True(info.GetProperty("retentionDays").GetInt32() >= 1);
        Assert.True(info.GetProperty("devices").GetInt32() >= 0);
    }

    private static async Task SetupAsync(HttpClient client)
    {
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest("users.owner", "a secure owner password"))).EnsureSuccessStatusCode();
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
