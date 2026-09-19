using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Agent.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Transport;

public sealed class AgentApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/agent/enroll", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Controller returned an empty enrollment response.");
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(DeviceIdentity identity, HeartbeatPayload payload, CancellationToken cancellationToken)
    {
        var message = Sign(identity, payload);
        using var response = await httpClient.PostAsJsonAsync("api/agent/heartbeat", message, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Controller returned an empty heartbeat response.");
    }

    public async Task SendOperationResultAsync(DeviceIdentity identity, OperationResultPayload result, CancellationToken cancellationToken)
    {
        var message = Sign(identity, result);
        using var response = await httpClient.PostAsJsonAsync("api/agent/operation-result", message, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AgentPendingTransfer[]> GetPendingTransfersAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        var message = Sign(identity, new TransferPendingRequest());
        using var response = await httpClient.PostAsJsonAsync("api/agent/transfers/pending", message, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentPendingTransfer[]>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task PushTransferChunkAsync(DeviceIdentity identity, TransferChunkPush chunk, CancellationToken cancellationToken)
    {
        var message = Sign(identity, chunk);
        using var response = await httpClient.PostAsJsonAsync("api/agent/transfers/push", message, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<TransferChunkResponse> PullTransferChunkAsync(DeviceIdentity identity, TransferChunkPull request, CancellationToken cancellationToken)
    {
        var message = Sign(identity, request);
        using var response = await httpClient.PostAsJsonAsync("api/agent/transfers/pull", message, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransferChunkResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Controller returned an empty transfer response.");
    }

    public async Task FailTransferAsync(DeviceIdentity identity, Guid transferId, string error, CancellationToken cancellationToken)
    {
        var message = Sign(identity, new TransferFailRequest(transferId, error.Length > 1024 ? error[..1024] : error));
        using var response = await httpClient.PostAsJsonAsync("api/agent/transfers/fail", message, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static SignedAgentMessage Sign<T>(DeviceIdentity identity, T payload)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.PrivateKey), out _);
        return AgentProtocol.Create(identity.DeviceId, key, payload);
    }
}
