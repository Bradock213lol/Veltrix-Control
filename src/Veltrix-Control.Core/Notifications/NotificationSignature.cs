using System.Security.Cryptography;
using System.Text;

namespace VeltrixControl.Core.Notifications;

public static class NotificationSignature
{
    public static string Compute(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
