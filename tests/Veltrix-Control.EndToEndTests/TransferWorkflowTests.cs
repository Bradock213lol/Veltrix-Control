using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.EndToEndTests;

public sealed class TransferWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task DownloadTransferStreamsFileThroughController()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, deviceKey) = await EnrollAsync(client, "download.owner");

        var transfer = await PostUiAsync<TransferView>(client, $"/api/devices/{deviceId:D}/transfers",
            new TransferRequest(TransferDirection.Download, "reports/summary.txt", 0, null));
        Assert.Equal(TransferState.Pending, transfer.State);

        var pending = await PostAgentAsync<AgentPendingTransfer[]>(client, "/api/agent/transfers/pending",
            AgentProtocol.Create(deviceId, deviceKey, new TransferPendingRequest()));
        var item = Assert.Single(pending);
        Assert.Equal(TransferDirection.Download, item.Direction);

        var content = Encoding.UTF8.GetBytes("quarterly report contents");
        var sha = Convert.ToHexString(SHA256.HashData(content));
        await PostAgentAsync<TransferSyncResponse>(client, "/api/agent/transfers/push",
            AgentProtocol.Create(deviceId, deviceKey, new TransferChunkPush(item.Id, 0, Convert.ToBase64String(content), true, sha)));

        var completed = await client.GetFromJsonAsync<TransferView>($"/api/transfers/{item.Id:D}", JsonOptions);
        Assert.Equal(TransferState.Completed, completed!.State);
        Assert.Equal(content.Length, completed.TotalBytes);
        Assert.Equal(sha, completed.Sha256);

        var bytes = await client.GetByteArrayAsync($"/api/transfers/{item.Id:D}/chunks?offset=0&count=1024");
        Assert.Equal(content, bytes);
    }

    [Fact]
    public async Task DownloadTransferRejectsChecksumMismatch()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, deviceKey) = await EnrollAsync(client, "checksum.owner");

        var transfer = await PostUiAsync<TransferView>(client, $"/api/devices/{deviceId:D}/transfers",
            new TransferRequest(TransferDirection.Download, "file.bin", 0, new string('B', 64)));
        var content = Encoding.UTF8.GetBytes("payload");
        using var push = await client.PostAsJsonAsync("/api/agent/transfers/push",
            AgentProtocol.Create(deviceId, deviceKey, new TransferChunkPush(transfer.Id, 0, Convert.ToBase64String(content), true, new string('A', 64))), JsonOptions);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, push.StatusCode);

        var failed = await client.GetFromJsonAsync<TransferView>($"/api/transfers/{transfer.Id:D}", JsonOptions);
        Assert.Equal(TransferState.Failed, failed!.State);
    }

    [Fact]
    public async Task UploadTransferChunksAreDeliveredToAgent()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, deviceKey) = await EnrollAsync(client, "upload.owner");

        var content = Encoding.UTF8.GetBytes("uploaded configuration value");
        var sha = Convert.ToHexString(SHA256.HashData(content));
        var transfer = await PostUiAsync<TransferView>(client, $"/api/devices/{deviceId:D}/transfers",
            new TransferRequest(TransferDirection.Upload, "config/app.json", content.Length, sha));

        foreach (var (offset, chunk) in new[] { (0, content[..10]), (10, content[10..]) })
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/transfers/{transfer.Id:D}/chunks?offset={offset}")
            {
                Content = new ByteArrayContent(chunk)
            };
            put.Headers.Add("X-Veltrix-Control-Request", "ui");
            using var response = await client.SendAsync(put);
            response.EnsureSuccessStatusCode();
        }

        var active = await client.GetFromJsonAsync<TransferView>($"/api/transfers/{transfer.Id:D}", JsonOptions);
        Assert.Equal(TransferState.Active, active!.State);
        Assert.Equal(content.Length, active.BytesTransferred);

        var pending = await PostAgentAsync<AgentPendingTransfer[]>(client, "/api/agent/transfers/pending",
            AgentProtocol.Create(deviceId, deviceKey, new TransferPendingRequest()));
        var item = Assert.Single(pending);

        var pull = await PostAgentAsync<TransferChunkResponse>(client, "/api/agent/transfers/pull",
            AgentProtocol.Create(deviceId, deviceKey, new TransferChunkPull(item.Id, 0, 256 * 1024)));
        Assert.True(pull.Last);
        Assert.Equal(content, Convert.FromBase64String(pull.DataBase64));

        var completed = await client.GetFromJsonAsync<TransferView>($"/api/transfers/{transfer.Id:D}", JsonOptions);
        Assert.Equal(TransferState.Completed, completed!.State);
    }

    [Fact]
    public async Task UploadPullRejectsOutOfSequenceOffset()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, deviceKey) = await EnrollAsync(client, "sequence.owner");

        var transfer = await PostUiAsync<TransferView>(client, $"/api/devices/{deviceId:D}/transfers",
            new TransferRequest(TransferDirection.Upload, "config/sequenced.bin", 64, null));

        using var pull = await client.PostAsJsonAsync("/api/agent/transfers/pull",
            AgentProtocol.Create(deviceId, deviceKey, new TransferChunkPull(transfer.Id, 5, 256 * 1024)), JsonOptions);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, pull.StatusCode);
    }

    [Fact]
    public async Task ViewerCannotCreateTransfers()
    {
        using var factory = new EndToEndFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var (deviceId, deviceKey) = await EnrollAsync(client, "viewer.owner");
        _ = deviceKey;

        var dbPath = Path.Combine(factory.DataDirectory, "controller.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO users(id, username, password_hash, role, created_at) VALUES ($id, 'viewer.user', $hash, 'Viewer', $now);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$hash", PasswordHasher.Hash("a secure viewer password"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("viewer.user", "a secure viewer password"), options: JsonOptions)
        })
        {
            using var response = await viewer.SendAsync(login);
            response.EnsureSuccessStatusCode();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/devices/{deviceId:D}/transfers")
        {
            Content = JsonContent.Create(new TransferRequest(TransferDirection.Upload, "blocked.txt", 4, null), options: JsonOptions)
        };
        request.Headers.Add("X-Veltrix-Control-Request", "ui");
        using var forbidden = await viewer.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static async Task<(Guid DeviceId, ECDsa Key)> EnrollAsync(HttpClient client, string owner)
    {
        (await client.PostAsJsonAsync("/api/setup", new SetupRequest(owner, "a secure transfer password"))).EnsureSuccessStatusCode();
        var token = await PostUiAsync<EnrollmentTokenResponse>(client, "/api/enrollment/tokens", new CreateEnrollmentTokenRequest(15));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inventory = new HardwareInventory("Windows Simulator", "10.0", "X64", 8, 16_000, [], "0.3.0", true);
        using var response = await client.PostAsJsonAsync("/api/agent/enroll",
            new EnrollmentRequest(token.Code, "Transfer-Node", AgentProtocol.ExportPublicKey(key), inventory), JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrollment = (await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions))!;
        return (enrollment.DeviceId, key);
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
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }
}
