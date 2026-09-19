using System.Security.Claims;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class UserEndpoints
{
    public static void MapManagementUsers(this RouteGroupBuilder management)
    {
        management.MapGet("/users", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.AdminManage) ? Results.Ok(await database.GetUsersAsync(ct)) : Results.Forbid());

        management.MapPost("/users", async (CreateUserRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length is < 3 or > 64 || !request.Username.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
                return Results.BadRequest(new { error = "Username must be 3-64 characters and use letters, numbers, dots, dashes, or underscores." });
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length is < 12 or > 256)
                return Results.BadRequest(new { error = "Password must be between 12 and 256 characters." });
            if (!UserRoles.IsKnown(request.Role)) return Results.BadRequest(new { error = "The role is invalid." });
            var created = await database.CreateUserAsync(user.Identity!.Name!, request, PasswordHasher.Hash(request.Password), ct);
            return created is null ? Results.Conflict(new { error = "A user with that name already exists." }) : Results.Ok(created);
        });

        management.MapPut("/users/{userId:guid}/role", async (Guid userId, UpdateUserRoleRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            if (!UserRoles.IsKnown(request.Role)) return Results.BadRequest(new { error = "The role is invalid." });
            try
            {
                return await database.UpdateUserRoleAsync(userId, request.Role, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapPost("/users/{userId:guid}/password", async (Guid userId, ResetPasswordRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length is < 12 or > 256)
                return Results.BadRequest(new { error = "Password must be between 12 and 256 characters." });
            return await database.ResetUserPasswordAsync(userId, PasswordHasher.Hash(request.Password), user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapDelete("/users/{userId:guid}", async (Guid userId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            try
            {
                return await database.DeleteUserAsync(userId, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapGet("/system/info", async (ClaimsPrincipal user, VeltrixControlStore database, Microsoft.Extensions.Options.IOptions<ControllerOptions> options, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            var controller = options.Value;
            var counts = await database.GetSystemCountsAsync(TimeSpan.FromSeconds(controller.OfflineAfterSeconds), ct);
            var version = typeof(UserEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.10.0";
            return Results.Ok(new
            {
                version,
                uptimeSeconds = Environment.TickCount64 / 1000,
                devices = counts.Devices,
                onlineDevices = counts.OnlineDevices,
                openAlerts = counts.OpenAlerts,
                queuedOperations = counts.QueuedOperations,
                retentionDays = controller.RetentionDays,
                dataDirectory = controller.DataDirectory
            });
        });
    }
}
