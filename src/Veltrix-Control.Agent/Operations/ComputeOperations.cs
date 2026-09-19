using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class ComputeOperations(ILogger<ComputeOperations> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<Guid, Process> _running = new();

    public static bool Handles(OperationKind kind) => kind is OperationKind.RunComputeJob or OperationKind.CancelComputeJob;

    public Task<OperationResultPayload> ExecuteAsync(OperationAssignment operation, CancellationToken cancellationToken) =>
        operation.Kind == OperationKind.RunComputeJob
            ? RunAsync(operation, cancellationToken)
            : Task.FromResult(Cancel(operation));

    private async Task<OperationResultPayload> RunAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        ComputeJobArgument argument;
        try
        {
            argument = Deserialize<ComputeJobArgument>(operation);
            Validate(argument);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            return Failure(operation.Id, exception.Message);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = argument.Executable,
                Arguments = argument.Arguments ?? string.Empty,
                WorkingDirectory = argument.WorkingDirectory ?? Path.GetDirectoryName(argument.Executable) ?? Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Windows did not start the job process.");

            if (!_running.TryAdd(argument.JobId, process))
            {
                process.Kill(entireProcessTree: true);
                return Failure(operation.Id, "A job with that identifier is already running on this node.");
            }

            try
            {
                var output = new StringBuilder();
                process.OutputDataReceived += (_, args) => Append(output, args.Data);
                process.ErrorDataReceived += (_, args) => Append(output, args.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var timeout = Math.Clamp(argument.TimeoutSeconds, ComputeLimits.MinTimeoutSeconds, ComputeLimits.MaxTimeoutSeconds);
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeout));
                var timedOut = false;
                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }

                var result = new ComputeJobResult(argument.JobId, timedOut ? -1 : process.ExitCode, output.ToString(), timedOut);
                return Success(operation.Id, result);
            }
            finally
            {
                _running.TryRemove(argument.JobId, out _);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            LogComputeFailed(logger, exception, operation.Id);
            return Failure(operation.Id, exception.Message);
        }
    }

    private OperationResultPayload Cancel(OperationAssignment operation)
    {
        ComputeCancelArgument argument;
        try
        {
            argument = Deserialize<ComputeCancelArgument>(operation);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Failure(operation.Id, exception.Message);
        }

        if (!_running.TryRemove(argument.JobId, out var process))
        {
            return Success(operation.Id, new { jobId = argument.JobId, cancelled = false, detail = "The job is not running on this node." });
        }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return Failure(operation.Id, exception.Message);
        }
        return Success(operation.Id, new { jobId = argument.JobId, cancelled = true });
    }

    private static void Validate(ComputeJobArgument argument)
    {
        if (argument.JobId == Guid.Empty) throw new ArgumentException("The job identifier is invalid.");
        if (string.IsNullOrWhiteSpace(argument.Executable) || argument.Executable.Length > 1024) throw new ArgumentException("A valid executable path is required.");
        if (argument.Executable.Contains('\0') || argument.Executable.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("The executable path is invalid.");
        if (!Path.IsPathRooted(argument.Executable)) throw new ArgumentException("The executable path must be absolute.");
        if (!File.Exists(argument.Executable)) throw new FileNotFoundException("The job executable does not exist.");
        if (!string.Equals(Path.GetExtension(argument.Executable), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only .exe programs can run as compute jobs.");
        if (argument.Arguments is { Length: > 2048 }) throw new ArgumentException("The job arguments exceed the allowed size.");
        if (argument.WorkingDirectory is { Length: > 1024 }) throw new ArgumentException("The working directory is invalid.");
        if (argument.WorkingDirectory is not null && !Directory.Exists(argument.WorkingDirectory)) throw new DirectoryNotFoundException("The working directory does not exist.");
    }

    private static readonly object OutputSync = new();

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null) return;
        lock (OutputSync)
        {
            if (builder.Length < ComputeLimits.MaxOutputLength)
            {
                builder.AppendLine(line);
            }
        }
    }

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(120, LogLevel.Warning, "Compute operation {operationId} failed")]
    private static partial void LogComputeFailed(ILogger logger, Exception exception, Guid operationId);
}
