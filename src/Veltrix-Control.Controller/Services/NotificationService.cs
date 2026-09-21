using System.Text.Json;
using VeltrixControl.Core.Notifications;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class NotificationService(
    VeltrixControlStore store,
    IntegrationCredentialProtector protector,
    ILogger<NotificationService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    private DateTimeOffset _lastCheck = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogDispatchFailed(logger, exception);
            }
        }
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetNotificationSettingsAsync(cancellationToken);
        var since = _lastCheck;
        _lastCheck = DateTimeOffset.UtcNow;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.WebhookUrl)) return;

        var alerts = await store.GetAlertsCreatedAfterAsync(since, cancellationToken);
        foreach (var alert in alerts)
        {
            var payload = JsonSerializer.Serialize(new
            {
                @event = "alert.raised",
                alert.Id,
                alert.DeviceId,
                alert.Severity,
                alert.Code,
                alert.Title,
                alert.Message,
                alert.CreatedAt
            }, JsonOptions);
            await SendAsync(settings.WebhookUrl, settings.Secret, payload, cancellationToken);
        }
    }

    public async Task<string> SendTestAsync(string webhookUrl, string? protectedSecret, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            @event = "test",
            message = "Veltrix-Control notification test",
            sentAt = DateTimeOffset.UtcNow
        }, JsonOptions);
        var secret = protectedSecret is null ? null : protector.Unprotect(protectedSecret);
        return await SendAsync(webhookUrl, secret, payload, cancellationToken);
    }

    private async Task<string> SendAsync(string webhookUrl, string? secret, string payload, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation("X-Veltrix-Signature", NotificationSignature.Compute(secret, payload));
        }
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = $"Webhook returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            LogDeliveryFailed(logger, webhookUrl, detail);
            return detail;
        }
        return "Webhook accepted the notification.";
    }

    [LoggerMessage(400, LogLevel.Warning, "Notification dispatch cycle failed")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(401, LogLevel.Warning, "Notification delivery to {url} failed: {detail}")]
    private static partial void LogDeliveryFailed(ILogger logger, string url, string detail);
}
