using System.Diagnostics;
using System.Text.Json;
using NexaGrid.Core.Files;
using NexaGrid.Contracts;

namespace NexaGrid.Agent.Operations;

public sealed partial class OperationExecutor(AgentOptions options, ILogger<OperationExecutor> logger)
{
    public async Task<OperationResultPayload> ExecuteAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        try
        {
            return operation.Kind switch
            {
                OperationKind.ListDirectory => ListDirectory(operation),
                OperationKind.Restart => await ExecutePowerAsync(operation, "/r /t 5 /d p:0:0 /c \"Authorized NexaGrid restart\"", cancellationToken),
                OperationKind.Shutdown => await ExecutePowerAsync(operation, "/s /t 5 /d p:0:0 /c \"Authorized NexaGrid shutdown\"", cancellationToken),
                _ => Failed(operation.Id, "Unsupported operation.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogOperationFailed(logger, exception, operation.Id, operation.Kind);
            return Failed(operation.Id, exception.Message);
        }
    }

    private OperationResultPayload ListDirectory(OperationAssignment operation)
    {
        var path = PathGuard.ResolveWithinRoot(options.ManagedRoot, operation.Argument);
        if (!Directory.Exists(path)) return Failed(operation.Id, "Directory does not exist.");
        var root = Path.GetFullPath(options.ManagedRoot);
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Take(1000)
            .Select(entry => new FileEntry(
                entry.Name,
                Path.GetRelativePath(root, entry.FullName),
                entry is DirectoryInfo,
                entry is FileInfo file ? file.Length : null,
                entry.LastWriteTimeUtc))
            .ToArray();
        return Succeeded(operation.Id, JsonSerializer.Serialize(entries));
    }

    private async Task<OperationResultPayload> ExecutePowerAsync(OperationAssignment operation, string arguments, CancellationToken cancellationToken)
    {
        if (!options.AllowPowerActions)
        {
            return Failed(operation.Id, "Power actions are disabled by this node's local policy.");
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null) return Failed(operation.Id, "Windows did not start the power operation.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? Succeeded(operation.Id, null) : Failed(operation.Id, $"shutdown.exe returned {process.ExitCode}.");
    }

    private static OperationResultPayload Succeeded(Guid id, string? json) =>
        new(id, OperationState.Succeeded, json, null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failed(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(1, LogLevel.Warning, "Operation {operationId} ({operationKind}) failed validation or I/O")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
