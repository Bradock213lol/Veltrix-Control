using System.Security.Claims;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Controller.Services;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class GovernanceEndpoints
{
    public static void MapManagementGovernance(this RouteGroupBuilder management)
    {
        management.MapGet("/devices/{deviceId:guid}/tags", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceView) ? Results.Ok(await database.GetDeviceTagsAsync(deviceId, ct)) : Results.Forbid());

        management.MapPost("/devices/{deviceId:guid}/tags", async (Guid deviceId, DeviceTagRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            if (!TagLimits.IsValidTag(request.Tag)) return Results.BadRequest(new { error = $"Tags must be 1-{TagLimits.MaxTagLength} characters using letters, numbers, spaces, dashes, or underscores." });
            try
            {
                await database.AddDeviceTagAsync(deviceId, request.Tag.Trim(), user.Identity!.Name!, ct);
                return Results.Ok(await database.GetDeviceTagsAsync(deviceId, ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapDelete("/devices/{deviceId:guid}/tags/{tag}", async (Guid deviceId, string tag, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            return await database.RemoveDeviceTagAsync(deviceId, tag, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapPost("/devices/{deviceId:guid}/favorite", async (Guid deviceId, bool? favorite, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceView)) return Results.Forbid();
            var wanted = favorite ?? true;
            return await database.SetDeviceFavoriteAsync(deviceId, wanted, user.Identity!.Name!, ct)
                ? Results.Ok(new { deviceId, favorite = wanted })
                : Results.NotFound();
        });

        management.MapGet("/settings/notifications", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            var settings = await database.GetNotificationSettingsAsync(ct);
            return Results.Ok(new NotificationSettingsView(settings.Enabled, settings.WebhookUrl, settings.Secret is not null, settings.UpdatedAt));
        });

        management.MapPut("/settings/notifications", async (NotificationSettingsRequest request, ClaimsPrincipal user, IntegrationCredentialProtector protector, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            if (request.Enabled && (string.IsNullOrWhiteSpace(request.WebhookUrl) || !Uri.TryCreate(request.WebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                return Results.BadRequest(new { error = "An HTTPS webhook URL is required when notifications are enabled." });
            if (request.WebhookUrl is { Length: > 512 }) return Results.BadRequest(new { error = "The webhook URL is too long." });
            if (request.Secret is { Length: > 256 }) return Results.BadRequest(new { error = "The secret is too long." });
            var protectedSecret = string.IsNullOrWhiteSpace(request.Secret) ? null : protector.Protect(request.Secret);
            await database.SaveNotificationSettingsAsync(user.Identity!.Name!, request, protectedSecret, ct);
            var saved = await database.GetNotificationSettingsAsync(ct);
            return Results.Ok(new NotificationSettingsView(saved.Enabled, saved.WebhookUrl, saved.Secret is not null, saved.UpdatedAt));
        });

        management.MapPost("/settings/notifications/test", async (NotificationService notifications, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            var settings = await database.GetNotificationSettingsAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.WebhookUrl)) return Results.BadRequest(new { error = "Configure a webhook URL first." });
            var detail = await notifications.SendTestAsync(settings.WebhookUrl, settings.Secret, ct);
            return Results.Ok(new { detail });
        });
    }
}
