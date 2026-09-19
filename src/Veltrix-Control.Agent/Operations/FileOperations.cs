using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Files;

namespace VeltrixControl.Agent.Operations;

public sealed partial class FileOperations(AgentOptions options, ILogger<FileOperations> logger)
{
    private const int MaxHistoryEntries = 10;
    private const int MaxSearchScanned = 200_000;
    private const long MaxArchiveUncompressedBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Handles(OperationKind kind) => kind is
        OperationKind.CreateDirectory or OperationKind.CreateFile or OperationKind.RenameFile or OperationKind.MoveFile or
        OperationKind.CopyFile or OperationKind.DeleteFile or OperationKind.SearchFiles or OperationKind.DirectorySize or
        OperationKind.CreateArchive or OperationKind.ExtractArchive or OperationKind.ReadTextFile or OperationKind.WriteTextFile or
        OperationKind.ListFileBackups or OperationKind.RestoreFileBackup or OperationKind.ReadFileBackup;

    public OperationResultPayload Execute(OperationAssignment operation)
    {
        try
        {
            return operation.Kind switch
            {
                OperationKind.CreateDirectory => CreateDirectory(operation),
                OperationKind.CreateFile => CreateFile(operation),
                OperationKind.RenameFile => Rename(operation),
                OperationKind.MoveFile => Move(operation),
                OperationKind.CopyFile => Copy(operation),
                OperationKind.DeleteFile => Delete(operation),
                OperationKind.SearchFiles => Search(operation),
                OperationKind.DirectorySize => DirectorySize(operation),
                OperationKind.CreateArchive => CreateArchive(operation),
                OperationKind.ExtractArchive => ExtractArchive(operation),
                OperationKind.ReadTextFile => ReadText(operation),
                OperationKind.WriteTextFile => WriteText(operation),
                OperationKind.ListFileBackups => ListBackups(operation),
                OperationKind.RestoreFileBackup => RestoreBackup(operation),
                OperationKind.ReadFileBackup => ReadBackup(operation),
                _ => Failure(operation.Id, "Unsupported file operation.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidDataException or CryptographicException)
        {
            LogFileOperationFailed(logger, exception, operation.Id, operation.Kind);
            return Failure(operation.Id, exception.Message);
        }
    }

    private OperationResultPayload CreateDirectory(OperationAssignment operation)
    {
        var path = Resolve(operation.Argument, allowRoot: false);
        if (Directory.Exists(path) || File.Exists(path)) return Failure(operation.Id, "The destination already exists.");
        Directory.CreateDirectory(path);
        return Success(operation.Id, new { path = Relative(path) });
    }

    private OperationResultPayload CreateFile(OperationAssignment operation)
    {
        var path = Resolve(operation.Argument, allowRoot: false);
        if (Directory.Exists(path)) return Failure(operation.Id, "A folder with that name already exists.");
        if (File.Exists(path)) return Failure(operation.Id, "The destination already exists.");
        using (File.Create(path)) { }
        return Success(operation.Id, new { path = Relative(path) });
    }

    private OperationResultPayload Rename(OperationAssignment operation)
    {
        var argument = Deserialize<TwoPathArgument>(operation);
        var source = Resolve(argument.Path, allowRoot: false);
        var destination = Resolve(argument.Destination, allowRoot: false);
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
        {
            return Failure(operation.Id, "Rename must keep the item in the same folder. Use move instead.");
        }
        return MoveCore(operation.Id, source, destination);
    }

    private OperationResultPayload Move(OperationAssignment operation)
    {
        var argument = Deserialize<TwoPathArgument>(operation);
        var source = Resolve(argument.Path, allowRoot: false);
        var destination = Resolve(argument.Destination, allowRoot: false);
        return MoveCore(operation.Id, source, destination);
    }

    private OperationResultPayload MoveCore(Guid operationId, string source, string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination)) return Failure(operationId, "The destination already exists.");
        if (PathGuard.IsWithin(source, destination)) return Failure(operationId, "The destination cannot be inside the source.");
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            return Failure(operationId, "The source does not exist.");
        }
        return Success(operationId, new { path = Relative(destination) });
    }

    private OperationResultPayload Copy(OperationAssignment operation)
    {
        var argument = Deserialize<TwoPathArgument>(operation);
        var source = Resolve(argument.Path, allowRoot: true);
        var destination = Resolve(argument.Destination, allowRoot: false);
        if (File.Exists(destination) || Directory.Exists(destination)) return Failure(operation.Id, "The destination already exists.");
        if (PathGuard.IsWithin(source, destination)) return Failure(operation.Id, "The destination cannot be inside the source.");

        if (File.Exists(source))
        {
            File.Copy(source, destination);
        }
        else if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
        else
        {
            return Failure(operation.Id, "The source does not exist.");
        }
        return Success(operation.Id, new { path = Relative(destination) });
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Reparse points are not supported by directory copy.");
            }
            CopyDirectory(directory, Path.Combine(destination, info.Name));
        }
    }

    private OperationResultPayload Delete(OperationAssignment operation)
    {
        var path = Resolve(operation.Argument, allowRoot: false);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            EnsureNoReparsePoints(path);
            Directory.Delete(path, recursive: true);
        }
        else
        {
            return Failure(operation.Id, "The item does not exist.");
        }
        return Success(operation.Id, new { path = Relative(path) });
    }

    private static void EnsureNoReparsePoints(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Reparse points are not supported by recursive delete.");
            }
            if (entry is DirectoryInfo child) EnsureNoReparsePoints(child.FullName);
        }
    }

    private OperationResultPayload Search(OperationAssignment operation)
    {
        var argument = Deserialize<SearchArgument>(operation);
        if (string.IsNullOrWhiteSpace(argument.Query) || argument.Query.Length > 128) throw new ArgumentException("The search query is invalid.");
        if (argument.Query.Contains(':') || argument.Query.Contains('\\') || argument.Query.Contains('/')) throw new ArgumentException("The search query is invalid.");
        var start = Resolve(argument.Path, allowRoot: true);
        if (!Directory.Exists(start)) return Failure(operation.Id, "The search folder does not exist.");

        var matcher = BuildMatcher(argument.Query);
        var matches = new List<FileEntry>();
        var truncated = false;
        var scanned = 0;
        var root = Path.GetFullPath(options.ManagedRoot);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                scanned++;
                if (scanned > MaxSearchScanned || matches.Count >= FileOperationLimits.MaxSearchResults)
                {
                    truncated = true;
                    break;
                }
                if (matcher(entry.Name))
                {
                    matches.Add(new FileEntry(
                        entry.Name,
                        Path.GetRelativePath(root, entry.FullName),
                        entry is DirectoryInfo,
                        entry is FileInfo file ? file.Length : null,
                        entry.LastWriteTimeUtc));
                }
                if (argument.Recursive && entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child.FullName);
                }
            }
            if (truncated) break;
        }

        return Success(operation.Id, new SearchFilesResult(matches, truncated));
    }

    private static Func<string, bool> BuildMatcher(string query)
    {
        if (query.Contains('*') || query.Contains('?'))
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(query)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";
            return name => System.Text.RegularExpressions.Regex.IsMatch(
                name, regex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        return name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private OperationResultPayload DirectorySize(OperationAssignment operation)
    {
        var start = Resolve(operation.Argument, allowRoot: true);
        if (!Directory.Exists(start)) return Failure(operation.Id, "The folder does not exist.");
        long total = 0;
        var files = 0;
        var directories = 0;
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directories++;
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is FileInfo file)
                {
                    total += file.Length;
                    files++;
                    if (files > 500_000) throw new InvalidDataException("The folder is too large to size.");
                }
                else if (entry is DirectoryInfo child)
                {
                    pending.Push(child.FullName);
                }
            }
        }
        return Success(operation.Id, new DirectorySizeResult(total, files, directories));
    }

    private OperationResultPayload CreateArchive(OperationAssignment operation)
    {
        var argument = Deserialize<TwoPathArgument>(operation);
        var source = Resolve(argument.Path, allowRoot: true);
        var archivePath = Resolve(argument.Destination, allowRoot: false);
        if (!File.Exists(source) && !Directory.Exists(source)) return Failure(operation.Id, "The source does not exist.");
        if (File.Exists(archivePath) || Directory.Exists(archivePath)) return Failure(operation.Id, "The destination already exists.");

        var root = Path.GetFullPath(options.ManagedRoot);
        var entryCount = 0;
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        try
        {
            if (File.Exists(source))
            {
                AddArchiveEntry(archive, root, source);
                entryCount++;
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
                        entryCount++;
                        if (entryCount > FileOperationLimits.MaxArchiveEntries) throw new InvalidDataException("The archive exceeds the entry limit.");
                        AddArchiveEntry(archive, root, file.FullName);
                    }
                }
            }
        }
        catch
        {
            archive.Dispose();
            TryDelete(archivePath);
            throw;
        }

        var length = new FileInfo(archivePath).Length;
        return Success(operation.Id, new ArchiveResult(Relative(archivePath), entryCount, length));
    }

    private static void AddArchiveEntry(ZipArchive archive, string root, string file)
    {
        var name = Path.GetRelativePath(root, file).Replace('\\', '/');
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var input = File.OpenRead(file);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private OperationResultPayload ExtractArchive(OperationAssignment operation)
    {
        var argument = Deserialize<TwoPathArgument>(operation);
        var archivePath = Resolve(argument.Path, allowRoot: true);
        var destination = Resolve(argument.Destination, allowRoot: false);
        if (!File.Exists(archivePath)) return Failure(operation.Id, "The archive does not exist.");
        if (File.Exists(destination) || Directory.Exists(destination)) return Failure(operation.Id, "The destination already exists.");

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > FileOperationLimits.MaxArchiveEntries) return Failure(operation.Id, "The archive exceeds the entry limit.");
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
                if (extracted > MaxArchiveUncompressedBytes) throw new InvalidDataException("The archive exceeds the size limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch
        {
            TryDeleteDirectory(destination);
            throw;
        }

        return Success(operation.Id, new ArchiveResult(Relative(destination), archive.Entries.Count, extracted));
    }

    private OperationResultPayload ReadText(OperationAssignment operation)
    {
        var path = Resolve(operation.Argument, allowRoot: false);
        if (!File.Exists(path)) return Failure(operation.Id, "The file does not exist.");
        var info = new FileInfo(path);
        if (info.Length > FileOperationLimits.MaxTextFileBytes) return Failure(operation.Id, "The file is too large for the editor.");

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        if (Array.IndexOf(bytes, (byte)0) >= 0) return Failure(operation.Id, "The file does not appear to be text.");

        using var memory = new MemoryStream(bytes);
        var encoding = PathGuard.DetectEncoding(memory, out var preamble);
        var content = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return Success(operation.Id, new TextFileResult(Relative(path), content, hash, bytes.Length, encoding.WebName, info.LastWriteTimeUtc));
    }

    private OperationResultPayload WriteText(OperationAssignment operation)
    {
        var argument = Deserialize<WriteTextFileArgument>(operation);
        var path = Resolve(argument.Path, allowRoot: false);
        if (argument.Content.Length > FileOperationLimits.MaxTextFileBytes) return Failure(operation.Id, "The content exceeds the editor limit.");

        var exists = File.Exists(path);
        byte[]? existing = null;
        if (exists)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= FileOperationLimits.MaxTextFileBytes)
            {
                existing = new byte[stream.Length];
                stream.ReadExactly(existing);
            }
            var currentHash = Convert.ToHexString(SHA256.HashData(existing ?? []));
            if (argument.ExpectedHash is not null && !string.Equals(argument.ExpectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(operation.Id, "The file changed on the node. Reload it before saving.");
            }
        }

        if (exists && argument.CreateBackup)
        {
            CreateHistoryBackup(path);
        }

        using var memory = new MemoryStream(existing ?? []);
        var encoding = PathGuard.DetectEncoding(memory, out var preamble);
        var hasPreamble = preamble > 0;
        var preambleBytes = hasPreamble ? encoding.GetPreamble() : [];
        var contentBytes = encoding.GetBytes(argument.Content);
        var output = new byte[preambleBytes.Length + contentBytes.Length];
        preambleBytes.CopyTo(output, 0);
        contentBytes.CopyTo(output, preambleBytes.Length);

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".veltrix-new";
        File.WriteAllBytes(temporary, output);
        File.Move(temporary, path, overwrite: true);
        var hash = Convert.ToHexString(SHA256.HashData(output));
        return Success(operation.Id, new TextFileResult(Relative(path), argument.Content, hash, output.Length, encoding.WebName, DateTimeOffset.UtcNow));
    }

    private OperationResultPayload ListBackups(OperationAssignment operation)
    {
        var path = Resolve(operation.Argument, allowRoot: false);
        var directory = HistoryDirectory(path);
        var backups = new List<FileBackupEntry>();
        if (Directory.Exists(directory))
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.bak").OrderByDescending(file => file.Name).Take(MaxHistoryEntries))
            {
                backups.Add(new FileBackupEntry(file.Name, file.Length, file.LastWriteTimeUtc));
            }
        }
        return Success(operation.Id, new FileBackupListResult(Relative(path), backups));
    }

    private OperationResultPayload ReadBackup(OperationAssignment operation)
    {
        var argument = Deserialize<ReadFileBackupArgument>(operation);
        var path = Resolve(argument.Path, allowRoot: false);
        if (!IsBackupName(argument.BackupName)) return Failure(operation.Id, "The backup name is invalid.");
        var directory = HistoryDirectory(path);
        var backup = Path.Combine(directory, argument.BackupName);
        if (!PathGuard.IsWithin(directory, backup) || !File.Exists(backup)) return Failure(operation.Id, "The backup does not exist.");
        var info = new FileInfo(backup);
        if (info.Length > FileOperationLimits.MaxTextFileBytes) return Failure(operation.Id, "The backup is too large to compare.");
        byte[] bytes;
        using (var stream = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        if (Array.IndexOf(bytes, (byte)0) >= 0) return Failure(operation.Id, "The backup does not appear to be text.");
        using var memory = new MemoryStream(bytes);
        var encoding = PathGuard.DetectEncoding(memory, out var preamble);
        var content = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return Success(operation.Id, new TextFileResult(Relative(path), content, hash, bytes.Length, encoding.WebName, info.LastWriteTimeUtc));
    }

    private OperationResultPayload RestoreBackup(OperationAssignment operation)
    {
        var argument = Deserialize<RestoreFileBackupArgument>(operation);
        var path = Resolve(argument.Path, allowRoot: false);
        if (!IsBackupName(argument.BackupName)) return Failure(operation.Id, "The backup name is invalid.");
        var directory = HistoryDirectory(path);
        var backup = Path.Combine(directory, argument.BackupName);
        if (!PathGuard.IsWithin(directory, backup) || !File.Exists(backup)) return Failure(operation.Id, "The backup does not exist.");
        if (File.Exists(path)) CreateHistoryBackup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(backup, path, overwrite: true);
        return Success(operation.Id, new { path = Relative(path), backup = argument.BackupName });
    }

    private void CreateHistoryBackup(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > FileOperationLimits.MaxTextFileBytes) return;
        var directory = HistoryDirectory(path);
        Directory.CreateDirectory(directory);
        var name = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";
        File.Copy(path, Path.Combine(directory, name), overwrite: true);
        foreach (var stale in new DirectoryInfo(directory).EnumerateFiles("*.bak").OrderByDescending(file => file.Name).Skip(MaxHistoryEntries))
        {
            stale.Delete();
        }
    }

    private string HistoryDirectory(string path) =>
        Path.Combine(options.DataDirectory, "file-history", HashPath(path));

    private static string HashPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())));

    private static bool IsBackupName(string name) =>
        name.Length == 21 && name.EndsWith(".bak", StringComparison.Ordinal) &&
        name[..17].All(char.IsAsciiDigit);

    private string Resolve(string? relativePath, bool allowRoot) =>
        PathGuard.ResolveWithinRoot(options.ManagedRoot, relativePath, allowRoot);

    private string Relative(string fullPath) => PathGuard.RelativeToRoot(options.ManagedRoot, fullPath);

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [LoggerMessage(10, LogLevel.Warning, "File operation {operationId} ({operationKind}) failed")]
    private static partial void LogFileOperationFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
