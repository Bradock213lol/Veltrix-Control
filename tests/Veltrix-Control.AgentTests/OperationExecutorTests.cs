using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class OperationExecutorTests : IDisposable
{
    private readonly string _managedRoot = Path.Combine(Path.GetTempPath(), $"veltrix-control-tests-{Guid.NewGuid():N}");

    public OperationExecutorTests() => Directory.CreateDirectory(_managedRoot);

    [Theory]
    [InlineData(OperationKind.ListProcesses)]
    [InlineData(OperationKind.ListServices)]
    [InlineData(OperationKind.ListSoftware)]
    [InlineData(OperationKind.ListNetworkAdapters)]
    public async Task ReadOnlyDiagnosticsReturnJsonArrays(OperationKind kind)
    {
        var result = await CreateExecutor().ExecuteAsync(
            new OperationAssignment(Guid.NewGuid(), kind, null, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.ResultJson!);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }

    [Fact]
    public async Task DirectoryResultUsesWebJsonPropertyNames()
    {
        await File.WriteAllTextAsync(Path.Combine(_managedRoot, "example.txt"), "safe", CancellationToken.None);
        var result = await CreateExecutor().ExecuteAsync(
            new OperationAssignment(Guid.NewGuid(), OperationKind.ListDirectory, string.Empty, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Contains("\"relativePath\"", result.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"RelativePath\"", result.ResultJson, StringComparison.Ordinal);
    }

    private OperationExecutor CreateExecutor()
    {
        var options = new AgentOptions
        {
            ManagedRoot = _managedRoot,
            AllowPowerActions = false,
            DataDirectory = Path.Combine(_managedRoot, ".agent")
        };
        return new OperationExecutor(
            options,
            new FileOperations(options, NullLogger<FileOperations>.Instance),
            new AdminOperations(options, new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance), NullLogger<AdminOperations>.Instance),
            new SoftwareOperations(options, NullLogger<SoftwareOperations>.Instance),
            new WindowsUpdateOperations(options, NullLogger<WindowsUpdateOperations>.Instance),
            new BackupOperations(options, NullLogger<BackupOperations>.Instance),
            NullLogger<OperationExecutor>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_managedRoot)) Directory.Delete(_managedRoot, true);
    }
}
