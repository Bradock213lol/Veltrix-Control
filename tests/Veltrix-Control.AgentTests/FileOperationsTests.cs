using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class FileOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"veltrix-files-{Guid.NewGuid():N}");
    private readonly FileOperations _operations;

    public FileOperationsTests()
    {
        Directory.CreateDirectory(_root);
        var options = new AgentOptions
        {
            ManagedRoot = _root,
            DataDirectory = Path.Combine(_root, ".agent")
        };
        _operations = new FileOperations(options, NullLogger<FileOperations>.Instance);
    }

    [Fact]
    public void CreateReadWriteDeleteRoundTrip()
    {
        AssertSucceeded(Execute(OperationKind.CreateDirectory, "docs"));
        AssertSucceeded(Execute(OperationKind.CreateFile, "docs\\notes.txt"));

        var write = Execute(OperationKind.WriteTextFile, Json(new WriteTextFileArgument("docs\\notes.txt", "hello world", null, true)));
        AssertSucceeded(write);
        var written = Deserialize<TextFileResult>(write);

        var read = Execute(OperationKind.ReadTextFile, "docs\\notes.txt");
        AssertSucceeded(read);
        var content = Deserialize<TextFileResult>(read);
        Assert.Equal("hello world", content.Content);
        Assert.Equal(written.Hash, content.Hash);

        AssertSucceeded(Execute(OperationKind.DeleteFile, "docs\\notes.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "docs", "notes.txt")));
    }

    [Fact]
    public void WriteTextRejectsStaleHashAndKeepsBackup()
    {
        AssertSucceeded(Execute(OperationKind.CreateFile, "config.json"));
        AssertSucceeded(Execute(OperationKind.WriteTextFile, Json(new WriteTextFileArgument("config.json", "{\"a\":1}", null, true))));

        var stale = Execute(OperationKind.WriteTextFile, Json(new WriteTextFileArgument("config.json", "{\"a\":2}", new string('0', 64), true)));
        Assert.Equal(OperationState.Failed, stale.State);
        Assert.Contains("changed", stale.Error, StringComparison.OrdinalIgnoreCase);

        var current = Deserialize<TextFileResult>(Execute(OperationKind.ReadTextFile, "config.json"));
        var updated = Execute(OperationKind.WriteTextFile, Json(new WriteTextFileArgument("config.json", "{\"a\":2}", current.Hash, true)));
        AssertSucceeded(updated);

        var backups = Deserialize<FileBackupListResult>(Execute(OperationKind.ListFileBackups, "config.json"));
        Assert.Equal(2, backups.Backups.Count);

        var restored = Execute(OperationKind.RestoreFileBackup, Json(new RestoreFileBackupArgument("config.json", backups.Backups[0].BackupName)));
        AssertSucceeded(restored);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(_root, "config.json")));
    }

    [Fact]
    public void RenameMoveCopyAndSearchWorkWithinRoot()
    {
        AssertSucceeded(Execute(OperationKind.CreateDirectory, "src"));
        AssertSucceeded(Execute(OperationKind.CreateDirectory, "dst"));
        AssertSucceeded(Execute(OperationKind.CreateFile, "src\\alpha.txt"));

        AssertSucceeded(Execute(OperationKind.RenameFile, Json(new TwoPathArgument("src\\alpha.txt", "src\\beta.txt"))));
        AssertSucceeded(Execute(OperationKind.MoveFile, Json(new TwoPathArgument("src\\beta.txt", "dst\\beta.txt"))));
        AssertSucceeded(Execute(OperationKind.CopyFile, Json(new TwoPathArgument("dst\\beta.txt", "dst\\gamma.txt"))));

        var search = Deserialize<SearchFilesResult>(Execute(OperationKind.SearchFiles, Json(new SearchArgument(string.Empty, "*.txt", true))));
        Assert.Equal(2, search.Matches.Count);

        var size = Deserialize<DirectorySizeResult>(Execute(OperationKind.DirectorySize, string.Empty));
        Assert.Equal(2, size.FileCount);
    }

    [Fact]
    public void ArchiveRoundTripRestoresContent()
    {
        AssertSucceeded(Execute(OperationKind.CreateDirectory, "data"));
        AssertSucceeded(Execute(OperationKind.WriteTextFile, Json(new WriteTextFileArgument("data\\payload.txt", "archived", null, false))));

        AssertSucceeded(Execute(OperationKind.CreateArchive, Json(new TwoPathArgument("data", "data.zip"))));
        AssertSucceeded(Execute(OperationKind.DeleteFile, "data"));
        AssertSucceeded(Execute(OperationKind.ExtractArchive, Json(new TwoPathArgument("data.zip", "restored"))));

        Assert.Equal("archived", File.ReadAllText(Path.Combine(_root, "restored", "data", "payload.txt")));
    }

    [Fact]
    public void ExtractArchiveRejectsZipSlip()
    {
        var archivePath = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("escape");
        }

        var result = Execute(OperationKind.ExtractArchive, Json(new TwoPathArgument("evil.zip", "out")));
        Assert.Equal(OperationState.Failed, result.State);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }

    [Fact]
    public void TraversalIsRejectedForEveryMutation()
    {
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CreateDirectory, "..\\escape").State);
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CreateFile, "C:\\escape.txt").State);
        Assert.Equal(OperationState.Failed, Execute(OperationKind.DeleteFile, "..").State);
        Assert.Equal(OperationState.Failed, Execute(OperationKind.WriteTextFile, Json(new WriteTextFileArgument("..\\escape.txt", "x", null, false))).State);
    }

    [Fact]
    public void ReadTextRejectsBinaryFiles()
    {
        File.WriteAllBytes(Path.Combine(_root, "binary.bin"), [0x00, 0x01, 0x02, 0x03]);
        var result = Execute(OperationKind.ReadTextFile, "binary.bin");
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Contains("text", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateCreateAndMissingSourceFailClearly()
    {
        AssertSucceeded(Execute(OperationKind.CreateDirectory, "once"));
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CreateDirectory, "once").State);
        Assert.Equal(OperationState.Failed, Execute(OperationKind.CopyFile, Json(new TwoPathArgument("missing.txt", "copy.txt"))).State);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private OperationResultPayload Execute(OperationKind kind, string? argument) =>
        _operations.Execute(new OperationAssignment(Guid.NewGuid(), kind, argument, DateTimeOffset.UtcNow));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(OperationResultPayload result) =>
        JsonSerializer.Deserialize<T>(result.ResultJson!, JsonOptions)!;

    private static void AssertSucceeded(OperationResultPayload result)
    {
        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Null(result.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
