using System.Security.Claims;
using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class MonitoringEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapManagementMonitoring(this RouteGroupBuilder management)
    {
        management.MapPost("/devices/{deviceId:guid}/backups", async (Guid deviceId, BackupRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceBackup)) return Results.Forbid();
            var error = ValidateBackup(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var backup = await database.CreateBackupRecordAsync(deviceId, user.Identity!.Name!, request, ct);
                var argument = JsonSerializer.Serialize(new BackupArgument(backup.Id, request.SourcePath, request.DestinationPath), JsonOptions);
                var operation = await database.CreateOperationAsync(deviceId, user.Identity!.Name!, new OperationRequest(OperationKind.CreateBackup, argument, true), ct);
                return Results.Accepted($"/api/operations/{operation.Id:D}", new { backup, operation });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapGet("/devices/{deviceId:guid}/backups", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceBackup) ? Results.Ok(await database.GetBackupsAsync(deviceId, 100, ct)) : Results.Forbid());

        management.MapGet("/backups", async (int? limit, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceBackup) ? Results.Ok(await database.GetBackupsAsync(null, limit ?? 100, ct)) : Results.Forbid());

        management.MapPost("/backups/{backupId:guid}/restore", async (Guid backupId, RestoreBackupArgument request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceBackup)) return Results.Forbid();
            if (!ValidPath(request.DestinationPath)) return Results.BadRequest(new { error = "A valid restore destination is required." });
            var backup = await database.GetBackupAsync(backupId, ct);
            if (backup is null) return Results.NotFound();
            if (backup.State != "Completed") return Results.Conflict(new { error = "Only completed backups can be restored." });
            var argument = JsonSerializer.Serialize(new RestoreBackupArgument(backup.ArchivePath, request.DestinationPath), JsonOptions);
            var operation = await database.CreateOperationAsync(backup.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.RestoreBackup, argument, true), ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });

        management.MapPost("/backups/{backupId:guid}/verify", async (Guid backupId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceBackup)) return Results.Forbid();
            var backup = await database.GetBackupAsync(backupId, ct);
            if (backup is null) return Results.NotFound();
            var argument = JsonSerializer.Serialize(new VerifyBackupArgument(backup.ArchivePath), JsonOptions);
            var operation = await database.CreateOperationAsync(backup.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.VerifyBackup, argument, true), ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });

        management.MapGet("/alerts", async (string? state, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceView) ? Results.Ok(await database.GetAlertsAsync(state, 300, ct)) : Results.Forbid());

        management.MapPost("/alerts/{alertId:guid}/acknowledge", async (Guid alertId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceView)) return Results.Forbid();
            return await database.AcknowledgeAlertAsync(alertId, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapGet("/automations", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.AdminManage) ? Results.Ok(await database.GetAutomationsAsync(null, ct)) : Results.Forbid());

        management.MapPost("/automations", async (AutomationRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            var error = ValidateAutomation(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                return Results.Ok(await database.CreateAutomationAsync(user.Identity!.Name!, request, ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapPost("/automations/{automationId:guid}/enable", async (Guid automationId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            return await database.SetAutomationEnabledAsync(automationId, true, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapPost("/automations/{automationId:guid}/disable", async (Guid automationId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            return await database.SetAutomationEnabledAsync(automationId, false, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapDelete("/automations/{automationId:guid}", async (Guid automationId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            return await database.DeleteAutomationAsync(automationId, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapGet("/automations/runs", async (Guid? automationId, int? limit, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.AdminManage) ? Results.Ok(await database.GetAutomationRunsAsync(automationId, limit ?? 100, ct)) : Results.Forbid());
    }

    private static string? ValidateBackup(BackupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return "A backup name is required and must not exceed 128 characters.";
        if (!ValidPath(request.SourcePath)) return "A valid source path is required.";
        if (!ValidPath(request.DestinationPath)) return "A valid destination path is required.";
        if (request.RetentionDays is < 1 or > 3650) return "Retention must be between 1 and 3650 days.";
        return null;
    }

    private static bool ValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 && !path.Contains('\0') && !path.Contains(':');

    private static string? ValidateAutomation(AutomationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return "An automation name is required and must not exceed 128 characters.";
        if (request.Trigger is null || request.Action is null) return "A trigger and an action are required.";
        if (!MonitoringLimits.AutomationTriggers.Contains(request.Trigger.Kind, StringComparer.OrdinalIgnoreCase)) return "The trigger kind is invalid.";
        if (!MonitoringLimits.AutomationActions.Contains(request.Action.Kind, StringComparer.OrdinalIgnoreCase)) return "The action kind is invalid.";
        if (request.CooldownSeconds is < 30 or > MonitoringLimits.MaxCooldownSeconds) return $"The cooldown must be between 30 and {MonitoringLimits.MaxCooldownSeconds} seconds.";
        if (request.Trigger.Kind.Equals("Schedule", StringComparison.OrdinalIgnoreCase) &&
            request.Trigger.EveryMinutes is < MonitoringLimits.MinScheduleMinutes or > 7 * 24 * 60)
            return $"The schedule interval must be between {MonitoringLimits.MinScheduleMinutes} and 10080 minutes.";
        if (request.Trigger.Severity is not null && !MonitoringLimits.AlertSeverities.Contains(request.Trigger.Severity, StringComparer.OrdinalIgnoreCase))
            return "The alert severity is invalid.";
        if (request.Trigger.AlertCode is { Length: > 64 } || request.Action.AlertCode is { Length: > 64 } || request.Action.AlertTitle is { Length: > 128 })
            return "Alert metadata exceeds the allowed length.";
        if (request.Condition?.DeviceNameContains is { Length: > 128 } || request.Condition?.AlertCodeEquals is { Length: > 64 })
            return "The condition exceeds the allowed length.";
        if (request.Action.Kind.Equals("QueueOperation", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Action.OperationKind) ||
                !MonitoringLimits.AutomationOperationKinds.Contains(request.Action.OperationKind, StringComparer.OrdinalIgnoreCase))
                return "The automation operation must be one of: " + string.Join(", ", MonitoringLimits.AutomationOperationKinds) + ".";
        }
        return null;
    }
}
