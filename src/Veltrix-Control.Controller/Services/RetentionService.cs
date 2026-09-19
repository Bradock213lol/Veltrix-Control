using Microsoft.Extensions.Options;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class RetentionService(
    VeltrixControlStore store,
    IOptions<ControllerOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var removed = await store.PurgeOlderThanAsync(options.Value.RetentionDays, stoppingToken);
                if (removed > 0) LogPurged(logger, removed, options.Value.RetentionDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogPurgeFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(200, LogLevel.Information, "Retention removed {count} expired records older than {days} days")]
    private static partial void LogPurged(ILogger logger, int count, int days);

    [LoggerMessage(201, LogLevel.Warning, "Retention purge failed")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
