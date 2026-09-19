using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Core.Files;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class BackupOperations(AgentOptions options, ILogger<BackupOperations> logger)
{
    private const int MaxEntries = 100_000;
    private const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Handles(OperationKind kind) => kind is
        OperationKind.CreateBackup or OperationKind.RestoreBackup or OperationKind.VerifyBackup;

    public OperationResultPayload Execute(OperationAssignment operation)
    {
        try
        {
            return operation.Kind switch
            {
                OperationKind.CreateBackup => CreateBackup(operation),
                OperationKind.RestoreBackup => Restore(operation),
                OperationKind.VerifyBackup => Verify(operation),
                _ => Failure(operation.Id, "Unsupported backup operation.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidDataException or CryptographicException or NotSupportedException)
        {
            LogBackupFailed(logger, exception, operation.Id, operation.Kind);
            return Failure(operation.Id, exception.Message);
        }
    }

    private OperationResultPayload CreateBackup(OperationAssignment operation)
    {
        var argument = Deserialize<BackupArgument>(operation);
        var source = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.SourcePath, allowRoot: true);
        var archivePath = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.DestinationPath, allowRoot: false);
        if (!File.Exists(source) && !Directory.Exists(source)) return Failure(operation.Id, "The backup source does not exist.");
        if (File.Exists(archivePath) || Directory.Exists(archivePath)) return Failure(operation.Id, "The backup destination already exists.");
        if (PathGuard.IsWithin(source, archivePath)) return Failure(operation.Id, "The destination cannot be inside the source.");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        var entries = 0;
        var totalBytes = 0L;
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            if (File.Exists(source))
            {
                AddEntry(archive, options.ManagedRoot, source, ref entries, ref totalBytes);
            }
            else
            {
                var pending = new Stack<string>();
                pending.Push(source);
                while (pending.Count > 0)
                {
                    var directory = pending.Pop();
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        if (entry is DirectoryInfo child)
                        {
                            pending.Push(child.FullName);
                            continue;
                        }
                        if (entry is not FileInfo file) continue;
                        AddEntry(archive, options.ManagedRoot, file.FullName, ref entries, ref totalBytes);
                    }
                }
            }
        }

        var info = new FileInfo(archivePath);
        var sha = ComputeHash(archivePath);
        return Success(operation.Id, new BackupOperationResult(PathGuard.RelativeToRoot(options.ManagedRoot, archivePath), info.Length, sha, entries));
    }

    private static void AddEntry(ZipArchive archive, string root, string file, ref int entries, ref long totalBytes)
    {
        entries++;
        if (entries > MaxEntries) throw new InvalidDataException("The backup exceeds the entry limit.");
        totalBytes += new FileInfo(file).Length;
        if (totalBytes > MaxTotalBytes) throw new InvalidDataException("The backup exceeds the size limit.");
        var name = Path.GetRelativePath(root, file).Replace('\\', '/');
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var input = File.OpenRead(file);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private OperationResultPayload Restore(OperationAssignment operation)
    {
        var argument = Deserialize<RestoreBackupArgument>(operation);
        var archivePath = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.ArchivePath, allowRoot: true);
        var destination = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.DestinationPath, allowRoot: false);
        if (!File.Exists(archivePath)) return Failure(operation.Id, "The backup archive does not exist.");
        if (File.Exists(destination) || Directory.Exists(destination)) return Failure(operation.Id, "The restore destination already exists.");

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxEntries) return Failure(operation.Id, "The backup exceeds the entry limit.");
        Directory.CreateDirectory(destination);
        long extracted = 0;
        try
        {
            foreach (var entry in archive.Entries)
            {
                var target = PathGuard.ResolveArchiveEntry(destination, entry.FullName, allowRoot: true);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                extracted += entry.Length;
                if (extracted > MaxTotalBytes) throw new InvalidDataException("The backup exceeds the size limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
        return Success(operation.Id, new RestoreOperationResult(argument.ArchivePath, argument.DestinationPath, archive.Entries.Count));
    }

    private OperationResultPayload Verify(OperationAssignment operation)
    {
        var argument = Deserialize<VerifyBackupArgument>(operation);
        var archivePath = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.ArchivePath, allowRoot: true);
        if (!File.Exists(archivePath)) return Failure(operation.Id, "The backup archive does not exist.");

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > 0)
            {
                using var stream = entry.Open();
                var buffer = new byte[64 * 1024];
                while (stream.Read(buffer) > 0)
                {
                }
            }
        }
        var sha = ComputeHash(archivePath);
        return Success(operation.Id, new VerifyOperationResult(argument.ArchivePath, true, archive.Entries.Count, sha));
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(90, LogLevel.Warning, "Backup operation {operationId} ({operationKind}) failed")]
    private static partial void LogBackupFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
