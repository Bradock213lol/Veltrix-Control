using System.Security.Cryptography;
using NexaGrid.Contracts;
using NexaGrid.Core.Health;
using NexaGrid.Core.Security;

namespace NexaGrid.UnitTests;

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

    private sealed record TestPayload(int Value);
}
