using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class GameServerMonitor(VeltrixControlStore store, ILogger<GameServerMonitor> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogPollFailed(logger, exception);
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var servers = await store.GetGameServersAsync(null, cancellationToken);
        foreach (var server in servers.Where(item => item.State is "Running" or "Starting"))
        {
            var argument = JsonSerializer.Serialize(new GameServerOutputArgument(server.Id, long.MaxValue / 2), JsonOptions);
            try
            {
                await store.CreateOperationAsync(server.DeviceId, "gameserver-monitor", new OperationRequest(OperationKind.GameServerOutput, argument, true), cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                // The device was removed; server cleanup follows through the foreign key.
            }
        }
    }

    [LoggerMessage(180, LogLevel.Warning, "Game server monitor cycle failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
