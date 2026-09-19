using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class GameServerCoordinator(VeltrixControlStore store, ILogger<GameServerCoordinator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleResultAsync(OperationView operation, OperationResultPayload result, CancellationToken cancellationToken)
    {
        if (operation.Kind is not (OperationKind.GameServerProvision or OperationKind.GameServerUpdate or OperationKind.GameServerStart
            or OperationKind.GameServerStop or OperationKind.GameServerOutput or OperationKind.GameServerInput)) return;
        if (operation.Argument is null) return;

        Guid serverId;
        try
        {
            using var document = JsonDocument.Parse(operation.Argument);
            if (!document.RootElement.TryGetProperty("serverId", out var serverElement) || !serverElement.TryGetGuid(out serverId)) return;
        }
        catch (JsonException)
        {
            return;
        }

        var server = await store.GetGameServerAsync(serverId, cancellationToken);
        if (server is null) return;

        switch (operation.Kind)
        {
            case OperationKind.GameServerProvision:
            case OperationKind.GameServerUpdate:
                if (result.State == OperationState.Succeeded)
                {
                    var version = TryProvisionVersion(result.ResultJson) ?? server.Version;
                    await store.SetGameServerStateAsync(server.Id, "Stopped", null, version, cancellationToken);
                    await store.RecordGameServerEventAsync(server.Id, operation.Kind == OperationKind.GameServerProvision ? "provisioned" : "updated", version, cancellationToken);
                }
                else
                {
                    await store.SetGameServerStateAsync(server.Id, "Failed", null, null, cancellationToken);
                    await store.RecordGameServerEventAsync(server.Id, "failed", result.Error, cancellationToken);
                }
                break;

            case OperationKind.GameServerStart:
            case OperationKind.GameServerOutput:
            case OperationKind.GameServerInput:
                await HandleStatusAsync(server, result, cancellationToken);
                break;

            case OperationKind.GameServerStop:
                if (result.State == OperationState.Succeeded)
                {
                    await store.SetGameServerStateAsync(server.Id, "Stopped", false, null, cancellationToken);
                    await store.RecordGameServerEventAsync(server.Id, "stopped", null, cancellationToken);
                }
                else
                {
                    await store.RecordGameServerEventAsync(server.Id, "stop-failed", result.Error, cancellationToken);
                }
                break;
        }
    }

    private async Task HandleStatusAsync(GameServerView server, OperationResultPayload result, CancellationToken cancellationToken)
    {
        if (result.State != OperationState.Succeeded)
        {
            await store.SetGameServerStateAsync(server.Id, "Failed", false, null, cancellationToken);
            await store.RecordGameServerEventAsync(server.Id, "failed", result.Error, cancellationToken);
            return;
        }

        var status = TryStatus(result.ResultJson);
        if (status is null) return;
        if (!status.Exited)
        {
            await store.SetGameServerStateAsync(server.Id, "Running", true, null, cancellationToken);
            if (server.State != "Running")
            {
                await store.RecordGameServerEventAsync(server.Id, "started", null, cancellationToken);
            }
            return;
        }

        await store.SetGameServerStateAsync(server.Id, "Crashed", false, null, cancellationToken);
        await store.RecordGameServerEventAsync(server.Id, "crashed", $"exit={status.ExitCode}", cancellationToken);
        LogCrashed(logger, server.Id, server.Name, status.ExitCode);

        if (!server.AutoRestart)
        {
            await store.SetGameServerStateAsync(server.Id, "Failed", false, null, cancellationToken);
            return;
        }

        var recentStarts = await store.CountRecentGameServerStartsAsync(server.Id, GameServerLimits.CrashLoopWindowMinutes, cancellationToken);
        if (recentStarts >= GameServerLimits.CrashLoopThreshold)
        {
            await store.SetGameServerStateAsync(server.Id, "Failed", false, null, cancellationToken);
            await store.RaiseAlertAsync(server.DeviceId, "Critical", "gameserver.crashloop", "Game server crash loop",
                $"{server.Name} restarted {recentStarts} times within {GameServerLimits.CrashLoopWindowMinutes} minutes and was stopped.",
                $"server={server.Id:D}", cancellationToken);
            return;
        }

        try
        {
            var argument = JsonSerializer.Serialize(new GameServerStartArgument(server.Id, server.InstallPath, server.MemoryMb, server.Port), JsonOptions);
            await store.CreateOperationAsync(server.DeviceId, "gameserver-monitor", new OperationRequest(OperationKind.GameServerStart, argument, true), cancellationToken);
            await store.SetGameServerStateAsync(server.Id, "Starting", null, null, cancellationToken);
            await store.RecordGameServerEventAsync(server.Id, "restarting", $"attempt={recentStarts + 1}", cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            await store.SetGameServerStateAsync(server.Id, "Failed", false, null, cancellationToken);
        }
    }

    private static string? TryProvisionVersion(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GameServerStatusResult? TryStatus(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<GameServerStatusResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(170, LogLevel.Warning, "Game server {serverId} ({name}) exited with code {exitCode}")]
    private static partial void LogCrashed(ILogger logger, Guid serverId, string name, int? exitCode);
}
