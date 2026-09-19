using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class ComputeOperationsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NonExecutableAndUnsafePathsAreRejected()
    {
        var operations = new ComputeOperations(NullLogger<ComputeOperations>.Instance);
        var text = Path.Combine(Path.GetTempPath(), $"veltrix-compute-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(text, "text");
        try
        {
            Assert.Equal(OperationState.Failed, (await ExecuteAsync(operations, new ComputeJobArgument(Guid.NewGuid(), text, null, null, 60))).State);
            Assert.Equal(OperationState.Failed, (await ExecuteAsync(operations, new ComputeJobArgument(Guid.NewGuid(), @"\\server\share\job.exe", null, null, 60))).State);
            Assert.Equal(OperationState.Failed, (await ExecuteAsync(operations, new ComputeJobArgument(Guid.NewGuid(), "relative.exe", null, null, 60))).State);
            Assert.Equal(OperationState.Failed, (await ExecuteAsync(operations, new ComputeJobArgument(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "missing.exe"), null, null, 60))).State);
        }
        finally
        {
            File.Delete(text);
        }
    }

    [Fact]
    public async Task FixtureJobRunsAndReturnsExitCode()
    {
        var operations = new ComputeOperations(NullLogger<ComputeOperations>.Instance);
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var jobId = Guid.NewGuid();
        var result = await ExecuteAsync(operations, new ComputeJobArgument(jobId, ping, "-n 1 127.0.0.1", null, 60));
        Assert.Equal(OperationState.Succeeded, result.State);
        var job = JsonSerializer.Deserialize<ComputeJobResult>(result.ResultJson!, JsonOptions);
        Assert.NotNull(job);
        Assert.Equal(0, job!.ExitCode);
        Assert.False(job.TimedOut);
        Assert.Contains("127.0.0.1", job.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobTimeoutIsReported()
    {
        var operations = new ComputeOperations(NullLogger<ComputeOperations>.Instance);
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var result = await ExecuteAsync(operations, new ComputeJobArgument(Guid.NewGuid(), ping, "-n 30 127.0.0.1", null, 10));
        Assert.Equal(OperationState.Succeeded, result.State);
        var job = JsonSerializer.Deserialize<ComputeJobResult>(result.ResultJson!, JsonOptions);
        Assert.NotNull(job);
        Assert.True(job!.TimedOut);
        Assert.Equal(-1, job.ExitCode);
    }

    [Fact]
    public async Task CancelOfUnknownJobIsReportedAsNotRunning()
    {
        var operations = new ComputeOperations(NullLogger<ComputeOperations>.Instance);
        var result = await operations.ExecuteAsync(
            new OperationAssignment(Guid.NewGuid(), OperationKind.CancelComputeJob, Json(new ComputeCancelArgument(Guid.NewGuid())), DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Contains("false", result.ResultJson, StringComparison.Ordinal);
    }

    private static Task<OperationResultPayload> ExecuteAsync(ComputeOperations operations, ComputeJobArgument argument) =>
        operations.ExecuteAsync(new OperationAssignment(Guid.NewGuid(), OperationKind.RunComputeJob, Json(argument), DateTimeOffset.UtcNow), CancellationToken.None);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
