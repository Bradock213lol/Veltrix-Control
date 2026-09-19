using VeltrixControl.Agent.Security;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Transfers;

public sealed partial class TransferWorker(
    DeviceIdentityStore identityStore,
    TransferService transferService,
    ILogger<TransferWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = identityStore.Load();
                if (identity is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var processed = await transferService.ProcessPendingAsync(identity, stoppingToken);
                await Task.Delay(processed > 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                LogTransferCycleFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    [LoggerMessage(30, LogLevel.Warning, "Transfer cycle failed; retrying shortly")]
    private static partial void LogTransferCycleFailed(ILogger logger, Exception exception);
}
