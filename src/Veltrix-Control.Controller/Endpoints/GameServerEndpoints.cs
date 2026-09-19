using System.Security.Claims;
using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class GameServerEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapManagementGameServers(this RouteGroupBuilder management)
    {
        management.MapPost("/devices/{deviceId:guid}/gameservers", async (Guid deviceId, GameServerRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            var error = Validate(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var server = await database.CreateGameServerAsync(deviceId, user.Identity!.Name!, request, ct);
                var argument = JsonSerializer.Serialize(new GameServerProvisionArgument(server.Id, server.Adapter, server.InstallPath, server.Port, server.MemoryMb, server.Version), JsonOptions);
                var operation = await database.CreateOperationAsync(deviceId, user.Identity!.Name!, new OperationRequest(OperationKind.GameServerProvision, argument, true), ct);
                return Results.Accepted($"/api/gameservers/{server.Id:D}", new { server, operation });
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

        management.MapGet("/gameservers", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.GameManage) ? Results.Ok(await database.GetGameServersAsync(null, ct)) : Results.Forbid());

        management.MapGet("/devices/{deviceId:guid}/gameservers", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.GameManage) ? Results.Ok(await database.GetGameServersAsync(deviceId, ct)) : Results.Forbid());

        management.MapGet("/gameservers/{serverId:guid}", async (Guid serverId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            var server = await database.GetGameServerAsync(serverId, ct);
            return server is null ? Results.NotFound() : Results.Ok(server);
        });

        management.MapGet("/gameservers/{serverId:guid}/events", async (Guid serverId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.GameManage) ? Results.Ok(await database.GetGameServerEventsAsync(serverId, 100, ct)) : Results.Forbid());

        management.MapPost("/gameservers/{serverId:guid}/start", async (Guid serverId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            var server = await database.GetGameServerAsync(serverId, ct);
            if (server is null) return Results.NotFound();
            if (server.State is "Running" or "Starting") return Results.Conflict(new { error = "The game server is already running." });
            var argument = JsonSerializer.Serialize(new GameServerStartArgument(server.Id, server.InstallPath, server.MemoryMb, server.Port), JsonOptions);
            var operation = await database.CreateOperationAsync(server.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.GameServerStart, argument, true), ct);
            await database.SetGameServerStateAsync(server.Id, "Starting", null, null, ct);
            await database.RecordGameServerEventAsync(server.Id, "start-requested", null, ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });

        management.MapPost("/gameservers/{serverId:guid}/stop", async (Guid serverId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            var server = await database.GetGameServerAsync(serverId, ct);
            if (server is null) return Results.NotFound();
            if (server.State is "Stopped" or "Failed" or "Provisioning") return Results.Conflict(new { error = "The game server is not running." });
            var argument = JsonSerializer.Serialize(new GameServerStopArgument(server.Id), JsonOptions);
            var operation = await database.CreateOperationAsync(server.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.GameServerStop, argument, true), ct);
            await database.SetGameServerStateAsync(server.Id, "Stopping", null, null, ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });

        management.MapPost("/gameservers/{serverId:guid}/update", async (Guid serverId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            var server = await database.GetGameServerAsync(serverId, ct);
            if (server is null) return Results.NotFound();
            if (server.State is "Running" or "Starting" or "Stopping") return Results.Conflict(new { error = "Stop the game server before updating it." });
            var argument = JsonSerializer.Serialize(new GameServerUpdateArgument(server.Id, server.Adapter, server.InstallPath, server.Version), JsonOptions);
            var operation = await database.CreateOperationAsync(server.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.GameServerUpdate, argument, true), ct);
            await database.RecordGameServerEventAsync(server.Id, "update-requested", null, ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });

        management.MapPost("/gameservers/{serverId:guid}/console", async (Guid serverId, TerminalInputRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            if (string.IsNullOrEmpty(request.Data) || request.Data.Length > AdminLimits.MaxCommandLength)
                return Results.BadRequest(new { error = "The console command is empty or too long." });
            var server = await database.GetGameServerAsync(serverId, ct);
            if (server is null) return Results.NotFound();
            if (server.State is not ("Running" or "Starting")) return Results.Conflict(new { error = "The game server is not running." });
            var argument = JsonSerializer.Serialize(new GameServerInputArgument(server.Id, request.Data + (request.Data.EndsWith('\n') ? string.Empty : "\n")), JsonOptions);
            var operation = await database.CreateOperationAsync(server.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.GameServerInput, argument, true), ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });

        management.MapPost("/gameservers/{serverId:guid}/output", async (Guid serverId, long? since, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.GameManage)) return Results.Forbid();
            var server = await database.GetGameServerAsync(serverId, ct);
            if (server is null) return Results.NotFound();
            if (server.State is "Stopped" or "Failed" or "Provisioning") return Results.Conflict(new { error = "The game server is not running." });
            var argument = JsonSerializer.Serialize(new GameServerOutputArgument(server.Id, Math.Max(0, since ?? 0)), JsonOptions);
            var operation = await database.CreateOperationAsync(server.DeviceId, user.Identity!.Name!, new OperationRequest(OperationKind.GameServerOutput, argument, true), ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
        });
    }

    private static string? Validate(GameServerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return "A server name is required and must not exceed 128 characters.";
        if (!GameServerAdapters.IsKnown(request.Adapter)) return $"Adapter '{request.Adapter}' is not supported. Known adapters: {string.Join(", ", GameServerAdapters.Known)}.";
        if (string.IsNullOrWhiteSpace(request.InstallPath) || request.InstallPath.Length > 1024 || request.InstallPath.Contains('\0') || request.InstallPath.Contains(':'))
            return "A valid install path relative to the managed root is required.";
        if (request.Port is < 1 or > GameServerLimits.MaxPort) return "The port must be between 1 and 65535.";
        if (request.MemoryMb is < GameServerLimits.MinMemoryMb or > GameServerLimits.MaxMemoryMb) return $"Memory must be between {GameServerLimits.MinMemoryMb} and {GameServerLimits.MaxMemoryMb} MB.";
        if (request.CpuThreads is < 0 or > GameServerLimits.MaxThreads) return "The CPU thread count is outside the allowed range.";
        if (request.Version is { Length: > 64 }) return "The version is too long.";
        if (request.EnvironmentJson is { Length: > 2048 }) return "The environment configuration is too long.";
        return null;
    }
}
