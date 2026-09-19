namespace VeltrixControl.Contracts;

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferState
{
    Pending,
    Active,
    Completed,
    Failed,
    Cancelled
}

public sealed record TransferRequest(
    TransferDirection Direction,
    string Path,
    long TotalBytes,
    string? Sha256);

public sealed record TransferView(
    Guid Id,
    Guid DeviceId,
    TransferDirection Direction,
    TransferState State,
    string Path,
    long TotalBytes,
    long BytesTransferred,
    string? Sha256,
    string? Error,
    string RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record AgentPendingTransfer(
    Guid Id,
    TransferDirection Direction,
    string Path,
    long TotalBytes,
    long BytesTransferred,
    string? Sha256);

public sealed record TransferChunkPush(
    Guid TransferId,
    long Offset,
    string DataBase64,
    bool Last,
    string? Sha256);

public sealed record TransferChunkPull(
    Guid TransferId,
    long Offset,
    int MaxBytes);

public sealed record TransferChunkResponse(
    long Offset,
    string DataBase64,
    bool Last);

public sealed record TransferPendingRequest();

public sealed record TransferSyncResponse(
    long BytesTransferred,
    TransferState State);

public sealed record TransferFailRequest(
    Guid TransferId,
    string Error);

public sealed record PathArgument(string Path);

public sealed record TwoPathArgument(string Path, string Destination);

public sealed record SearchArgument(string Path, string Query, bool Recursive);

public sealed record WriteTextFileArgument(
    string Path,
    string Content,
    string? ExpectedHash,
    bool CreateBackup);

public sealed record RestoreFileBackupArgument(string Path, string BackupName);

public sealed record ReadFileBackupArgument(string Path, string BackupName);

public sealed record TextFileResult(
    string Path,
    string Content,
    string Hash,
    long SizeBytes,
    string Encoding,
    DateTimeOffset LastModified);

public sealed record SearchFilesResult(
    IReadOnlyList<FileEntry> Matches,
    bool Truncated);

public sealed record DirectorySizeResult(
    long TotalBytes,
    long FileCount,
    long DirectoryCount);

public sealed record ArchiveResult(
    string Path,
    int EntryCount,
    long UncompressedBytes);

public sealed record FileBackupEntry(
    string BackupName,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record FileBackupListResult(
    string Path,
    IReadOnlyList<FileBackupEntry> Backups);

public static class FileOperationLimits
{
    public const int MaxPathLength = 1024;
    public const int MaxTransferChunkBytes = 512 * 1024;
    public const long MaxTextFileBytes = 2 * 1024 * 1024;
    public const int MaxEditorContentLength = 1_000_000;
    public const int MaxSearchResults = 500;
    public const int MaxArchiveEntries = 20_000;
    public const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxTransferBytes = 16L * 1024 * 1024 * 1024;
}
