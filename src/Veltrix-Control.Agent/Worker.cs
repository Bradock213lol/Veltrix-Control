using System.Security.Cryptography;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Agent.Security;
using VeltrixControl.Agent.Telemetry;
using VeltrixControl.Agent.Transport;
using VeltrixControl.Core.Security;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent;

public sealed partial class Worker(
    DeviceIdentityStore identityStore,
    WindowsHardwareProbe hardwareProbe,
    AgentApiClient apiClient,
    OperationInbox OperationInbox,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retrySeconds = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = identityStore.Load() ?? await EnrollAsync(stoppingToken);
                var inventory = WindowsHardwareProbe.ReadInventory();
                var payload = new HeartbeatPayload(Environment.MachineName, inventory, hardwareProbe.ReadTelemetry());
                var response = await apiClient.HeartbeatAsync(identity, payload, stoppingToken);
                foreach (var operation in response.Operations)
                {
                    LogQueued(logger, operation.Id, operation.Kind);
                    await OperationInbox.EnqueueAsync(operation, stoppingToken);
                }
                retrySeconds = 2;
                await Task.Delay(TimeSpan.FromSeconds(response.NextHeartbeatSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException or InvalidOperationException)
            {
                LogCycleFailed(logger, exception, retrySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stoppingToken);
                retrySeconds = Math.Min(retrySeconds * 2, 60);
            }
        }
    }

    private async Task<DeviceIdentity> EnrollAsync(CancellationToken cancellationToken)
    {
        var enrollmentCode = identityStore.TakeEnrollmentCode()
            ?? throw new InvalidOperationException("This node is not enrolled. Place a one-time code in the configured enrollment-code file.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new EnrollmentRequest(
            enrollmentCode,
            Environment.MachineName,
            AgentProtocol.ExportPublicKey(key),
            WindowsHardwareProbe.ReadInventory());
        var response = await apiClient.EnrollAsync(request, cancellationToken);
        var identity = new DeviceIdentity(response.DeviceId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        identityStore.Save(identity);
        identityStore.RemoveEnrollmentCode();
        LogEnrolled(logger, identity.DeviceId);
        return identity;
    }

    [LoggerMessage(1, LogLevel.Information, "Queued operation {operationId} ({operationKind})")]
    private static partial void LogQueued(ILogger logger, Guid operationId, OperationKind operationKind);

    [LoggerMessage(2, LogLevel.Warning, "Agent cycle failed; reconnecting in {retrySeconds} seconds")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception, int retrySeconds);

    [LoggerMessage(3, LogLevel.Information, "Node enrolled as device {deviceId}")]
    private static partial void LogEnrolled(ILogger logger, Guid deviceId);
}
