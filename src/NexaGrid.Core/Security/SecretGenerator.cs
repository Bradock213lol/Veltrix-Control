using System.Security.Cryptography;

namespace NexaGrid.Core.Security;

public static class SecretGenerator
{
    public static string CreateEnrollmentCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[19];
        var bytes = RandomNumberGenerator.GetBytes(16);
        var output = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index is 4 or 8 or 12)
            {
                chars[output++] = '-';
            }
            chars[output++] = alphabet[bytes[index] % alphabet.Length];
        }
        return new string(chars);
    }

    public static string HashToken(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
