using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class BackupOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"veltrix-backup-{Guid.NewGuid():N}");
    private readonly BackupOperations _operations;

    public BackupOperationsTests()
    {
        Directory.CreateDirectory(_root);
        var options = new AgentOptions { ManagedRoot = _root, DataDirectory = Path.Combine(_root, ".agent") };
        _operations = new BackupOperations(options, NullLogger<BackupOperations>.Instance);
    }

    [Fact]
    public void BackupVerifyAndRestoreRoundTrip()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "nested"));
        File.WriteAllText(Path.Combine(_root, "data", "report.txt"), "quarterly report");
        File.WriteAllText(Path.Combine(_root, "data", "nested", "values.json"), "{\"value\":42}");

        var backup = Execute(OperationKind.CreateBackup, new BackupArgument(Guid.NewGuid(), "data", "backups\\data-001.zip"));
        Assert.Equal(OperationState.Succeeded, backup.State);
        var created = Deserialize<BackupOperationResult>(backup);
        Assert.Equal(2, created.EntryCount);
        Assert.True(created.SizeBytes > 0);
        Assert.Equal(64, created.Sha256.Length);

        var verify = Execute(OperationKind.VerifyBackup, new VerifyBackupArgument(created.ArchivePath));
        Assert.Equal(OperationState.Succeeded, verify.State);
        Assert.True(Deserialize<VerifyOperationResult>(verify).Valid);

        var restore = Execute(OperationKind.RestoreBackup, new RestoreBackupArgument(created.ArchivePath, "restored"));
        Assert.Equal(OperationState.Succeeded, restore.State);
        Assert.Equal("quarterly report", File.ReadAllText(Path.Combine(_root, "restored", "data", "report.txt")));
        Assert.Equal("{\"value\":42}", File.ReadAllText(Path.Combine(_root, "restored", "data", "nested", "values.json")));
    }

    [Fact]
    public void BackupOutsideManagedRootIsRejected()
    {
        var result = Execute(OperationKind.CreateBackup, new BackupArgument(Guid.NewGuid(), "..\\..", "backups\\x.zip"));
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CreateBackup, new BackupArgument(Guid.NewGuid(), "data", "C:\\escape.zip")).State);
    }

    [Fact]
    public void ExistingDestinationAndMissingSourceFail()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CreateBackup, new BackupArgument(Guid.NewGuid(), "missing", "backups\\x.zip")).State);
        File.WriteAllText(Path.Combine(_root, "backups.zip"), "occupied");
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CreateBackup, new BackupArgument(Guid.NewGuid(), "data", "backups.zip")).State);
    }

    [Fact]
    public void RestoreRejectsArchiveEscapesAndExistingDestination()
    {
        Directory.CreateDirectory(Path.Combine(_root, "archive-source"));
        File.WriteAllText(Path.Combine(_root, "archive-source", "file.txt"), "safe");
        var backup = Deserialize<BackupOperationResult>(Execute(OperationKind.CreateBackup, new BackupArgument(Guid.NewGuid(), "archive-source", "archive.zip")));

        Directory.CreateDirectory(Path.Combine(_root, "occupied"));
        var occupied = Execute(OperationKind.RestoreBackup, new RestoreBackupArgument(backup.ArchivePath, "occupied"));
        Assert.Equal(OperationState.Failed, occupied.State);

        var missing = Execute(OperationKind.RestoreBackup, new RestoreBackupArgument("missing.zip", "target"));
        Assert.Equal(OperationState.Failed, missing.State);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private OperationResultPayload Execute(OperationKind kind, object argument) =>
        _operations.Execute(new OperationAssignment(Guid.NewGuid(), kind,
            System.Text.Json.JsonSerializer.Serialize(argument, JsonOptions),
            DateTimeOffset.UtcNow));

    private static T Deserialize<T>(OperationResultPayload result) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(result.ResultJson!, JsonOptions)!;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
