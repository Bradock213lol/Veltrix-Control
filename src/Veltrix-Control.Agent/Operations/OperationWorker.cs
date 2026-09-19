using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Agent.Security;
using VeltrixControl.Agent.Transport;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class OperationWorker(
    OperationInbox queue,
    DeviceIdentityStore identityStore,
    OperationExecutor executor,
    AgentApiClient apiClient,
    AgentOptions options,
    ILogger<OperationWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private string PendingDirectory => Path.Combine(options.DataDirectory, "pending-results");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var operation in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var identity = identityStore.Load();
                if (identity is null)
                {
                    LogNotEnrolled(logger, operation.Id);
                    continue;
                }

                await FlushPendingAsync(identity, stoppingToken);
                var result = await executor.ExecuteAsync(operation, stoppingToken);
                await DeliverAsync(identity, result, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                LogExecutionFailed(logger, exception, operation.Id);
            }
        }
    }

    private async Task DeliverAsync(DeviceIdentity identity, OperationResultPayload result, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        var delay = TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await apiClient.SendOperationResultAsync(identity, result, cancellationToken);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }

        Persist(result);
        LogDeferred(logger, result.OperationId);
    }

    private void Persist(OperationResultPayload result)
    {
        try
        {
            Directory.CreateDirectory(PendingDirectory);
            var path = Path.Combine(PendingDirectory, $"{result.OperationId:D}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogDeferredPersistFailed(logger, exception, result.OperationId);
        }
    }

    private async Task FlushPendingAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(PendingDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(PendingDirectory, "*.json").Take(20))
        {
            try
            {
                var result = JsonSerializer.Deserialize<OperationResultPayload>(await File.ReadAllTextAsync(file, cancellationToken), JsonOptions);
                if (result is null)
                {
                    File.Delete(file);
                    continue;
                }
                await apiClient.SendOperationResultAsync(identity, result, cancellationToken);
                File.Delete(file);
            }
            catch (HttpRequestException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                LogDeferredFlushFailed(logger, exception, file);
            }
        }
    }

    [LoggerMessage(60, LogLevel.Warning, "Operation {operationId} cannot run because this node is not enrolled yet")]
    private static partial void LogNotEnrolled(ILogger logger, Guid operationId);

    [LoggerMessage(61, LogLevel.Error, "Operation {operationId} failed to execute")]
    private static partial void LogExecutionFailed(ILogger logger, Exception exception, Guid operationId);

    [LoggerMessage(62, LogLevel.Warning, "Result for operation {operationId} was stored locally for later delivery")]
    private static partial void LogDeferred(ILogger logger, Guid operationId);

    [LoggerMessage(63, LogLevel.Error, "Could not store the pending result for operation {operationId}")]
    private static partial void LogDeferredPersistFailed(ILogger logger, Exception exception, Guid operationId);

    [LoggerMessage(64, LogLevel.Warning, "Could not deliver a stored result from {file}")]
    private static partial void LogDeferredFlushFailed(ILogger logger, Exception exception, string file);
}
