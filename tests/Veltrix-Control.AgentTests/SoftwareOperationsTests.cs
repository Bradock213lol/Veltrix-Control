using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class SoftwareOperationsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(OperationKind.InstallSoftware)]
    [InlineData(OperationKind.UninstallSoftware)]
    [InlineData(OperationKind.UpgradeSoftware)]
    public void SoftwareOperationsAreRecognized(OperationKind kind) => Assert.True(SoftwareOperations.Handles(kind));

    [Theory]
    [InlineData(OperationKind.ScanWindowsUpdates)]
    [InlineData(OperationKind.InstallWindowsUpdate)]
    public void WindowsUpdateOperationsAreRecognized(OperationKind kind) => Assert.True(WindowsUpdateOperations.Handles(kind));

    [Fact]
    public async Task MissingWinGetIsReportedClearly()
    {
        var operations = Create();
        var result = await ExecuteAsync(operations, OperationKind.InstallSoftware,
            Json(new SoftwareInstallArgument(SoftwareSource.Winget, "vendor.tool", null, null, null, null)));
        if (result.State == OperationState.Succeeded)
        {
            Assert.NotNull(result.ResultJson);
            return;
        }
        Assert.Contains("WinGet", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafePackageArgumentsAreRejected()
    {
        var operations = Create();
        var injected = await ExecuteAsync(operations, OperationKind.InstallSoftware,
            Json(new SoftwareInstallArgument(SoftwareSource.Winget, "bad\"id", null, null, null, null)));
        Assert.Equal(OperationState.Failed, injected.State);

        var insecureUrl = await ExecuteAsync(operations, OperationKind.InstallSoftware,
            Json(new SoftwareInstallArgument(SoftwareSource.Msi, "http://example.invalid/tool.msi", null, "http://example.invalid/tool.msi", new string('A', 64), null)));
        Assert.Equal(OperationState.Failed, insecureUrl.State);
        Assert.Contains("HTTPS", insecureUrl.Error, StringComparison.OrdinalIgnoreCase);

        var missingHash = await ExecuteAsync(operations, OperationKind.InstallSoftware,
            Json(new SoftwareInstallArgument(SoftwareSource.Exe, "https://example.invalid/tool.exe", null, "https://example.invalid/tool.exe", null, "/S")));
        Assert.Equal(OperationState.Failed, missingHash.State);
        Assert.Contains("checksum", missingHash.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonWingetUninstallIsRejected()
    {
        var operations = Create();
        var result = await ExecuteAsync(operations, OperationKind.UninstallSoftware,
            Json(new SoftwareUninstallArgument(SoftwareSource.Msi, "https://example.invalid/tool.msi", "Tool")));
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Contains("WinGet", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsUpdateInstallRejectsMalformedIdentifiers()
    {
        var operations = new WindowsUpdateOperations(CreateOptions(), NullLogger<WindowsUpdateOperations>.Instance);
        var empty = await operations.ExecuteAsync(
            new OperationAssignment(Guid.NewGuid(), OperationKind.InstallWindowsUpdate, Json(new WindowsUpdateInstallArgument([])), DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(OperationState.Failed, empty.State);

        var malformed = await operations.ExecuteAsync(
            new OperationAssignment(Guid.NewGuid(), OperationKind.InstallWindowsUpdate, Json(new WindowsUpdateInstallArgument(["not-a-guid"])), DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(OperationState.Failed, malformed.State);
        Assert.Contains("identifiers", malformed.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static SoftwareOperations Create() => new(CreateOptions(), NullLogger<SoftwareOperations>.Instance);

    private static AgentOptions CreateOptions() => new()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), $"veltrix-software-{Guid.NewGuid():N}")
    };

    private static Task<OperationResultPayload> ExecuteAsync(SoftwareOperations operations, OperationKind kind, string argument) =>
        operations.ExecuteAsync(new OperationAssignment(Guid.NewGuid(), kind, argument, DateTimeOffset.UtcNow), CancellationToken.None);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
