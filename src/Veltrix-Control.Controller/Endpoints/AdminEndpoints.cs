using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Network;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void MapManagementAdmin(this RouteGroupBuilder management)
    {
        management.MapPost("/devices/{deviceId:guid}/terminal", async (Guid deviceId, StartTerminalRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceTerminal)) return Results.Forbid();
            if (!Enum.IsDefined(request.Shell)) return Results.BadRequest(new { error = "The requested shell is invalid." });
            if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || request.WorkingDirectory.Length > 1024 || request.WorkingDirectory.Contains('\0'))
                return Results.BadRequest(new { error = "A valid working directory is required." });
            if (await database.CountActiveTerminalSessionsAsync(deviceId, ct) >= AdminLimits.MaxTerminalSessionsPerDevice)
                return Results.Conflict(new { error = "The device already has the maximum number of terminal sessions." });

            try
            {
                var session = await database.CreateTerminalSessionAsync(deviceId, user.Identity!.Name!, request.Shell, request.WorkingDirectory, ct);
                var operation = await database.CreateOperationAsync(deviceId, user.Identity!.Name!,
                    new OperationRequest(OperationKind.TerminalStart, JsonSerializer.Serialize(new TerminalStartArgument(session.Id, request.Shell.ToString(), request.WorkingDirectory), JsonOptions), true), ct);
                return Results.Accepted($"/api/terminal/{session.Id:D}", new TerminalStartResponse(session, operation));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        management.MapGet("/devices/{deviceId:guid}/terminal", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceTerminal)) return Results.Forbid();
            return Results.Ok(await database.GetTerminalSessionsAsync(deviceId, 50, ct));
        });

        management.MapPost("/terminal/{sessionId:guid}/input", async (Guid sessionId, TerminalInputRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceTerminal)) return Results.Forbid();
            if (string.IsNullOrEmpty(request.Data) || request.Data.Length > AdminLimits.MaxCommandLength)
                return Results.BadRequest(new { error = "The command is empty or exceeds the allowed length." });
            var session = await database.GetTerminalSessionAsync(sessionId, ct);
            if (session is null) return Results.NotFound();
            if (session.State != "Active") return Results.Conflict(new { error = "The terminal session is closed." });
            var operation = await database.CreateOperationAsync(session.DeviceId, user.Identity!.Name!,
                new OperationRequest(OperationKind.TerminalInput, JsonSerializer.Serialize(new TerminalInputArgument(sessionId, request.Data), JsonOptions), true), ct);
            await database.RecordAuditEventAsync(user.Identity!.Name!, "device.terminal.input", sessionId.ToString("D"), "queued", $"length={request.Data.Length}", ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", new TerminalOperationResponse(session, operation));
        });

        management.MapPost("/terminal/{sessionId:guid}/output", async (Guid sessionId, long? since, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceTerminal)) return Results.Forbid();
            var session = await database.GetTerminalSessionAsync(sessionId, ct);
            if (session is null) return Results.NotFound();
            var operation = await database.CreateOperationAsync(session.DeviceId, user.Identity!.Name!,
                new OperationRequest(OperationKind.TerminalOutput, JsonSerializer.Serialize(new TerminalOutputArgument(sessionId, Math.Max(0, since ?? 0)), JsonOptions), true), ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", new TerminalOperationResponse(session, operation));
        });

        management.MapPost("/terminal/{sessionId:guid}/stop", async (Guid sessionId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceTerminal)) return Results.Forbid();
            var session = await database.GetTerminalSessionAsync(sessionId, ct);
            if (session is null) return Results.NotFound();
            var operation = await database.CreateOperationAsync(session.DeviceId, user.Identity!.Name!,
                new OperationRequest(OperationKind.TerminalStop, JsonSerializer.Serialize(new TerminalStopArgument(sessionId), JsonOptions), true), ct);
            await database.CloseTerminalSessionAsync(sessionId, user.Identity!.Name!, ct);
            return Results.Accepted($"/api/operations/{operation.Id:D}", new TerminalOperationResponse(session with { State = "Closed" }, operation));
        });

        management.MapPost("/devices/{deviceId:guid}/wake", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DevicePower)) return Results.Forbid();
            var inventory = await database.GetDeviceInventoryAsync(deviceId, ct);
            if (inventory is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(inventory.MacAddress))
                return Results.BadRequest(new { error = "The node has not reported a MAC address, so Wake-on-LAN is unavailable." });
            try
            {
                await WakeOnLan.SendAsync(inventory.MacAddress, ct);
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException)
            {
                return Results.BadRequest(new { error = $"Wake-on-LAN could not be sent: {exception.Message}" });
            }
            await database.RecordAuditEventAsync(user.Identity!.Name!, "device.wake", deviceId.ToString("D"), "succeeded", $"mac={inventory.MacAddress}", ct);
            return Results.Ok(new { sent = true, macAddress = inventory.MacAddress });
        });
    }
}
