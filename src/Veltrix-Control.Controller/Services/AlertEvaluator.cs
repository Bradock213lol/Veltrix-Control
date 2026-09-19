using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class AlertEvaluator(VeltrixControlStore store, ILogger<AlertEvaluator> logger) : BackgroundService
{
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(EvaluationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogEvaluationFailed(logger, exception);
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var devices = await store.GetDevicesAsync(TimeSpan.FromSeconds(90), cancellationToken);
        foreach (var device in devices)
        {
            if (!device.Online)
            {
                await store.RaiseAlertAsync(device.Id, "Critical", "device.offline", "Device offline",
                    $"{device.Name} has not reported telemetry within the expected window.", null, cancellationToken);
                continue;
            }
            await store.AutoResolveAlertAsync(device.Id, "device.offline", cancellationToken);

            var currentVersion = typeof(AlertEvaluator).Assembly.GetName().Version?.ToString(3);
            if (!device.Inventory.IsSimulation && currentVersion is not null &&
                !string.Equals(device.Inventory.AgentVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                await store.RaiseAlertAsync(device.Id, "Info", "agent.outdated", "Agent update available",
                    $"{device.Name} reports agent {device.Inventory.AgentVersion}; Controller is {currentVersion}.", null, cancellationToken);
            }
            else
            {
                await store.AutoResolveAlertAsync(device.Id, "agent.outdated", cancellationToken);
            }

            var telemetry = device.Telemetry;
            if (telemetry is null) continue;

            if (telemetry.CpuPercent > 95)
            {
                await store.RaiseAlertAsync(device.Id, "Warning", "device.cpu", "Sustained CPU pressure",
                    $"{device.Name} is at {telemetry.CpuPercent:0}% CPU.", null, cancellationToken);
            }
            else
            {
                await store.AutoResolveAlertAsync(device.Id, "device.cpu", cancellationToken);
            }

            if (telemetry.TotalMemoryBytes > 0)
            {
                var memoryPercent = telemetry.UsedMemoryBytes * 100d / telemetry.TotalMemoryBytes;
                if (memoryPercent > 95)
                {
                    await store.RaiseAlertAsync(device.Id, "Warning", "device.memory", "Memory pressure",
                        $"{device.Name} is using {memoryPercent:0}% of memory.", null, cancellationToken);
                }
                else
                {
                    await store.AutoResolveAlertAsync(device.Id, "device.memory", cancellationToken);
                }
            }

            var lowDisk = telemetry.Disks.FirstOrDefault(disk => disk.TotalBytes > 0 && disk.AvailableBytes * 100d / disk.TotalBytes < 10);
            if (lowDisk is not null)
            {
                await store.RaiseAlertAsync(device.Id, "Warning", "device.disk", "Low disk space",
                    $"{device.Name} has less than 10% free on {lowDisk.Name}.", $"drive={lowDisk.Name}", cancellationToken);
            }
            else
            {
                await store.AutoResolveAlertAsync(device.Id, "device.disk", cancellationToken);
            }
        }
    }

    [LoggerMessage(100, LogLevel.Warning, "Alert evaluation cycle failed")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception exception);
}
