using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeltrixControl.Contracts;

namespace VeltrixControl.Core.Security;

public static class AgentProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static SignedAgentMessage Create<T>(Guid deviceId, ECDsa signingKey, T payload, DateTimeOffset? now = null, string? nonce = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var actualNonce = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var encodedPayload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
        var signature = Convert.ToBase64String(signingKey.SignData(
            GetCanonicalBytes(deviceId, timestamp, actualNonce, encodedPayload),
            HashAlgorithmName.SHA256));
        return new SignedAgentMessage(deviceId, timestamp, actualNonce, encodedPayload, signature);
    }

    public static bool Verify(SignedAgentMessage message, string publicKey, DateTimeOffset now, TimeSpan maximumClockSkew)
    {
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(message.UnixTimeSeconds);
        if ((now - sentAt).Duration() > maximumClockSkew || message.Nonce.Length is < 16 or > 128)
        {
            return false;
        }

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return verifier.VerifyData(
                GetCanonicalBytes(message.DeviceId, message.UnixTimeSeconds, message.Nonce, message.Payload),
                Convert.FromBase64String(message.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static T? Decode<T>(SignedAgentMessage message) =>
        JsonSerializer.Deserialize<T>(Convert.FromBase64String(message.Payload), SerializerOptions);

    public static string ExportPublicKey(ECDsa key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static byte[] GetCanonicalBytes(Guid deviceId, long timestamp, string nonce, string payload) =>
        Encoding.UTF8.GetBytes($"{deviceId:D}\n{timestamp}\n{nonce}\n{payload}");
}
