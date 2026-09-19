using System.Security.Cryptography;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Health;
using VeltrixControl.Core.Security;

namespace VeltrixControl.UnitTests;

public sealed class SecurityAndDomainTests
{
    [Fact]
    public void PasswordHashRoundTripAcceptsOnlyCorrectPassword()
    {
        var hash = PasswordHasher.Hash("a sufficiently long password");
        Assert.True(PasswordHasher.Verify("a sufficiently long password", hash));
        Assert.False(PasswordHasher.Verify("a different long password", hash));
        Assert.DoesNotContain("sufficiently", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrollmentCodesAreReadableAndUnique()
    {
        var codes = Enumerable.Range(0, 100).Select(_ => SecretGenerator.CreateEnrollmentCode()).ToArray();
        Assert.Equal(100, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){3}$", code));
    }

    [Theory]
    [InlineData("Owner", "admin.manage", true)]
    [InlineData("Administrator", "device.power", true)]
    [InlineData("Operator", "device.files", true)]
    [InlineData("Viewer", "device.diagnostics", true)]
    [InlineData("Viewer", "device.power", false)]
    [InlineData("Unknown", "device.view", false)]
    public void RolePermissionsAreExplicit(string role, string permission, bool expected)
    {
        Assert.Equal(expected, RolePermissions.HasPermission(role, permission));
    }

    [Fact]
    public void SignedAgentMessageVerifiesAndDecodes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var payload = new { Value = 42 };
        var message = AgentProtocol.Create(Guid.NewGuid(), key, payload, now, "1234567890ABCDEF");
        Assert.True(AgentProtocol.Verify(message, AgentProtocol.ExportPublicKey(key), now, TimeSpan.FromMinutes(2)));
        Assert.Equal(42, AgentProtocol.Decode<TestPayload>(message)?.Value);
    }

    [Fact]
    public void SignedAgentMessageRejectsTamperingAndExpiry()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var message = AgentProtocol.Create(Guid.NewGuid(), key, new { Value = 42 }, sentAt, "1234567890ABCDEF");
        Assert.False(AgentProtocol.Verify(message, AgentProtocol.ExportPublicKey(key), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)));
        Assert.False(AgentProtocol.Verify(message with { Payload = message.Payload + "A" }, AgentProtocol.ExportPublicKey(key), sentAt, TimeSpan.FromMinutes(2)));
    }

    [Theory]
    [InlineData(20, 4_000, 8_000, true, 100)]
    [InlineData(95, 7_900, 8_000, true, 40)]
    [InlineData(20, 4_000, 8_000, false, 0)]
    public void HealthScoreReflectsAvailabilityAndPressure(double cpu, long used, long total, bool online, int expected)
    {
        var snapshot = new TelemetrySnapshot(DateTimeOffset.UtcNow, cpu, used, total, 10, []);
        Assert.Equal(expected, HealthScorer.Calculate(snapshot, online));
    }

    [Theory]
    [InlineData(OperationKind.StopProcess, "device.processes")]
    [InlineData(OperationKind.StartService, "device.services")]
    [InlineData(OperationKind.TerminalStart, "device.terminal")]
    [InlineData(OperationKind.ScheduleRestart, "device.power")]
    [InlineData(OperationKind.TerminalOutput, "device.terminal")]
    [InlineData(OperationKind.ListDirectory, "device.files")]
    public void OperationPermissionsAreMapped(OperationKind kind, string permission)
    {
        Assert.Equal(permission, RolePermissions.PermissionFor(kind));
        Assert.False(RolePermissions.HasPermission("Viewer", permission));
        Assert.True(RolePermissions.HasPermission("Administrator", permission));
    }

    [Theory]
    [InlineData("System", true)]
    [InlineData("csrss", true)]
    [InlineData("lsass", true)]
    [InlineData("explorer", false)]
    [InlineData("notepad", false)]
    public void ProtectedProcessPolicyIsExplicit(string name, bool expected)
    {
        Assert.Equal(expected, AdminLimits.IsProtectedProcess(name));
    }

    [Theory]
    [InlineData("RpcSs", true)]
    [InlineData("WinDefend", true)]
    [InlineData("Spooler", false)]
    public void ProtectedServicePolicyIsExplicit(string name, bool expected)
    {
        Assert.Equal(expected, AdminLimits.IsProtectedService(name));
    }

    [Fact]
    public void WakeOnLanPacketMatchesTheMagicPacketLayout()
    {
        var packet = VeltrixControl.Core.Network.WakeOnLan.BuildMagicPacket("00:11:22:AA:BB:CC");
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), value => Assert.Equal(0xFF, value));
        for (var repeat = 0; repeat < 16; repeat++)
        {
            Assert.Equal(0x00, packet[6 + repeat * 6]);
            Assert.Equal(0x11, packet[6 + repeat * 6 + 1]);
            Assert.Equal(0x22, packet[6 + repeat * 6 + 2]);
            Assert.Equal(0xAA, packet[6 + repeat * 6 + 3]);
            Assert.Equal(0xBB, packet[6 + repeat * 6 + 4]);
            Assert.Equal(0xCC, packet[6 + repeat * 6 + 5]);
        }
    }

    [Theory]
    [InlineData("00:11:22:33:44")]
    [InlineData("ZZ:11:22:33:44:55")]
    [InlineData("")]
    public void WakeOnLanRejectsInvalidMacAddresses(string mac)
    {
        Assert.Throws<ArgumentException>(() => VeltrixControl.Core.Network.WakeOnLan.BuildMagicPacket(mac));
    }

    private sealed record TestPayload(int Value);
}
