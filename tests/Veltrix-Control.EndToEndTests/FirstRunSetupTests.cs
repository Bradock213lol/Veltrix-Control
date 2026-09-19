using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;

namespace VeltrixControl.EndToEndTests;

public sealed class FirstRunSetupTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SetupStatusDrivesFirstRunAndCompletesExactlyOnce()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var status = await client.GetFromJsonAsync<SetupStatus>("/api/setup/status", JsonOptions);
        Assert.True(status!.Required);

        (await client.PostAsJsonAsync("/api/setup", new SetupRequest("first.owner", "a secure first owner password"))).EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<SetupStatus>("/api/setup/status", JsonOptions);
        Assert.False(after!.Required);

        using var second = await client.PostAsJsonAsync("/api/setup", new SetupRequest("second.owner", "another secure owner password"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", JsonOptions);
        Assert.Equal("first.owner", me.GetProperty("username").GetString());
        Assert.Equal("Owner", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task LoginWithoutSetupExplainsThatNoAccountExists()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "some password value"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginValidationMessagesAreSpecific()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var missingUsername = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(" ", "a secure password"));
        Assert.Equal(HttpStatusCode.BadRequest, missingUsername.StatusCode);
        var missingUsernameBody = await missingUsername.Content.ReadFromJsonAsync<ErrorEnvelope>(JsonOptions);
        Assert.Contains("username", missingUsernameBody!.Error, StringComparison.OrdinalIgnoreCase);

        using var missingPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("someone", string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, missingPassword.StatusCode);
        var missingPasswordBody = await missingPassword.Content.ReadFromJsonAsync<ErrorEnvelope>(JsonOptions);
        Assert.Contains("password", missingPasswordBody!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryAccountIsCreatedAfterSetupAndCanSignIn()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest("recovery.owner", "a secure recovery password"))).EnsureSuccessStatusCode();

        using var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin!"));
        login.EnsureSuccessStatusCode();
        var identity = await login.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("admin", identity.GetProperty("username").GetString());
        Assert.Equal("Administrator", identity.GetProperty("role").GetString());

        var users = await client.GetFromJsonAsync<UserView[]>("/api/users", JsonOptions);
        Assert.Contains(users!, user => user.Username == "admin" && user.Role == "Administrator");
        Assert.Contains(users!, user => user.Username == "recovery.owner" && user.Role == "Owner");
    }

    private sealed record SetupStatus(bool Required);
    private sealed record ErrorEnvelope(string? Error);
}
