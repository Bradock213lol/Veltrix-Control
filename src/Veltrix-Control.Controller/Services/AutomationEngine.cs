using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class AutomationEngine(VeltrixControlStore store, ILogger<AutomationEngine> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(20);

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
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogEvaluationFailed(logger, exception);
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var automations = await store.GetAutomationsAsync(enabled: true, cancellationToken);
        foreach (var automation in automations)
        {
            if (automation.LastRunAt is not null && automation.LastRunAt.Value.AddSeconds(automation.CooldownSeconds) > now) continue;
            var trigger = Deserialize<AutomationTriggerRequest>(automation.TriggerJson);
            if (trigger is null) continue;
            var condition = automation.ConditionJson is null ? null : Deserialize<AutomationConditionRequest>(automation.ConditionJson);
            var action = Deserialize<AutomationActionRequest>(automation.ActionJson);
            if (action is null) continue;

            var match = await MatchAsync(automation, trigger, condition, now, cancellationToken);
            if (match is null) continue;

            var (deviceId, detail, deviceName) = match.Value;
            if (condition?.DeviceNameContains is { Length: > 0 } filter && deviceName is not null &&
                !deviceName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var runState = await ExecuteActionAsync(automation, action, deviceId, detail, cancellationToken);
            await store.RecordAutomationRunAsync(automation.Id, deviceId, runState.State, runState.Detail, cancellationToken);
        }
    }

    private async Task<(Guid? DeviceId, string Detail, string? DeviceName)?> MatchAsync(
        AutomationView automation,
        AutomationTriggerRequest trigger,
        AutomationConditionRequest? condition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (trigger.Kind)
        {
            case "Schedule":
                var minutes = Math.Clamp(trigger.EveryMinutes ?? 60, MonitoringLimits.MinScheduleMinutes, 7 * 24 * 60);
                if (automation.LastRunAt is null || now - automation.LastRunAt.Value >= TimeSpan.FromMinutes(minutes))
                {
                    return (null, $"schedule={minutes}m", null);
                }
                return null;

            case "AlertRaised":
                var since = automation.LastRunAt ?? now.AddMinutes(-5);
                if (since < now.AddMinutes(-10)) since = now.AddMinutes(-10);
                var alerts = await store.GetAlertsCreatedAfterAsync(since, cancellationToken);
                var alert = alerts.FirstOrDefault(item =>
                    (trigger.AlertCode is null || string.Equals(item.Code, trigger.AlertCode, StringComparison.OrdinalIgnoreCase)) &&
                    (trigger.Severity is null || string.Equals(item.Severity, trigger.Severity, StringComparison.OrdinalIgnoreCase)) &&
                    (condition?.AlertCodeEquals is null || string.Equals(item.Code, condition.AlertCodeEquals, StringComparison.OrdinalIgnoreCase)) &&
                    (item.Metadata?.Contains("source=automation", StringComparison.OrdinalIgnoreCase) != true));
                return alert is null ? null : (alert.DeviceId, $"alert={alert.Code}", null);

            case "DeviceOffline":
            case "DeviceOnline":
                var wantsOnline = trigger.Kind == "DeviceOnline";
                var devices = await store.GetDevicesAsync(TimeSpan.FromSeconds(90), cancellationToken);
                var device = devices.FirstOrDefault(item =>
                    item.Online == wantsOnline &&
                    (condition?.DeviceNameContains is null || item.Name.Contains(condition.DeviceNameContains, StringComparison.OrdinalIgnoreCase)));
                return device is null ? null : (device.Id, $"device={device.Name}", device.Name);

            default:
                return null;
        }
    }

    private async Task<(string State, string Detail)> ExecuteActionAsync(
        AutomationView automation,
        AutomationActionRequest action,
        Guid? deviceId,
        string detail,
        CancellationToken cancellationToken)
    {
        var actor = $"automation:{automation.Name}";
        switch (action.Kind)
        {
            case "CreateAlert":
                var code = string.IsNullOrWhiteSpace(action.AlertCode) ? "automation.triggered" : action.AlertCode;
                var title = string.IsNullOrWhiteSpace(action.AlertTitle) ? $"Automation: {automation.Name}" : action.AlertTitle;
                await store.RaiseAlertAsync(deviceId, "Warning", code, title, $"Automation '{automation.Name}' ran ({detail}).", "source=automation", cancellationToken);
                return ("Succeeded", $"alert={code} {detail}");

            case "QueueOperation":
                if (deviceId is null) return ("Skipped", "The trigger did not identify a device.");
                var kindName = action.OperationKind ?? string.Empty;
                if (!MonitoringLimits.AutomationOperationKinds.Contains(kindName, StringComparer.OrdinalIgnoreCase) ||
                    !Enum.TryParse<OperationKind>(kindName, ignoreCase: true, out var kind))
                {
                    return ("Failed", $"Operation kind '{kindName}' is not allowed for automation.");
                }
                try
                {
                    var operation = await store.CreateOperationAsync(deviceId.Value, actor, new OperationRequest(kind, null, true), cancellationToken);
                    return ("Succeeded", $"operation={operation.Id:D} kind={kind} {detail}");
                }
                catch (KeyNotFoundException)
                {
                    return ("Failed", "The device no longer exists.");
                }

            default:
                return ("Failed", $"Action kind '{action.Kind}' is not supported.");
        }
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(110, LogLevel.Warning, "Automation evaluation cycle failed")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception exception);
}
