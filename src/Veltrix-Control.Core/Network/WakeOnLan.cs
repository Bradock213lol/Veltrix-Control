using System.Net;
using System.Net.Sockets;

namespace VeltrixControl.Core.Network;

public static class WakeOnLan
{
    public static byte[] BuildMagicPacket(string macAddress)
    {
        var mac = ParseMac(macAddress);
        var packet = new byte[6 + 16 * 6];
        for (var index = 0; index < 6; index++) packet[index] = 0xFF;
        for (var repeat = 0; repeat < 16; repeat++)
        {
            mac.CopyTo(packet, 6 + repeat * 6);
        }
        return packet;
    }

    public static byte[] ParseMac(string macAddress)
    {
        var cleaned = new string((macAddress ?? string.Empty).Where(char.IsAsciiHexDigit).ToArray());
        if (cleaned.Length != 12) throw new ArgumentException("The MAC address is invalid.");
        var bytes = new byte[6];
        for (var index = 0; index < 6; index++) bytes[index] = Convert.ToByte(cleaned.Substring(index * 2, 2), 16);
        return bytes;
    }

    public static async Task SendAsync(string macAddress, CancellationToken cancellationToken)
    {
        var packet = BuildMagicPacket(macAddress);
        cancellationToken.ThrowIfCancellationRequested();
        using var client = new UdpClient { EnableBroadcast = true };
        await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
    }
}
