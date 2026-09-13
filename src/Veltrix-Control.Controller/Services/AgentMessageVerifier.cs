using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;
using VeltrixControl.Contracts;

namespace VeltrixControl.Controller.Services;

public sealed class AgentMessageVerifier(VeltrixControlStore store, TimeProvider timeProvider)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public async Task<T?> VerifyAsync<T>(SignedAgentMessage message, CancellationToken cancellationToken)
    {
        var publicKey = await store.GetDevicePublicKeyAsync(message.DeviceId, cancellationToken);
        if (publicKey is null || !AgentProtocol.Verify(message, publicKey, timeProvider.GetUtcNow(), ClockSkew))
        {
            return default;
        }

        if (!await store.TryUseNonceAsync(message.DeviceId, message.Nonce, timeProvider.GetUtcNow(), cancellationToken))
        {
            return default;
        }

        try
        {
            return AgentProtocol.Decode<T>(message);
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
        {
            return default;
        }
    }
}
