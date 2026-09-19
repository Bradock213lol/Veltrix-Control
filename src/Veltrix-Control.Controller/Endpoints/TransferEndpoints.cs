using Microsoft.Extensions.Options;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Controller.Services;
using VeltrixControl.Controller.Validation;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class TransferEndpoints
{
    public static void MapAgentTransfers(this WebApplication app)
    {
        var agent = app.MapGroup("/api/agent/transfers").RequireRateLimiting("agent");

        agent.MapPost("/pending", async (SignedAgentMessage message, AgentMessageVerifier verifier, VeltrixControlStore database, CancellationToken ct) =>
        {
            var request = await verifier.VerifyAsync<TransferPendingRequest>(message, ct);
            if (request is null) return Results.Unauthorized();
            return Results.Ok(await database.GetPendingAgentTransfersAsync(message.DeviceId, ct));
        });

        agent.MapPost("/push", async (SignedAgentMessage message, AgentMessageVerifier verifier, VeltrixControlStore database, CancellationToken ct) =>
        {
            var chunk = await verifier.VerifyAsync<TransferChunkPush>(message, ct);
            if (chunk is null) return Results.Unauthorized();
            if (chunk.Offset < 0 || chunk.DataBase64 is null || chunk.DataBase64.Length > 1_400_000)
                return Results.BadRequest(new { error = "The transfer chunk is invalid." });

            byte[] data;
            try
            {
                data = Convert.FromBase64String(chunk.DataBase64);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "The transfer chunk is invalid." });
            }
            if (data.Length > FileOperationLimits.MaxTransferChunkBytes) return Results.BadRequest(new { error = "The transfer chunk is too large." });

            var transfer = await database.GetTransferAsync(chunk.TransferId, ct);
            if (transfer is null || transfer.DeviceId != message.DeviceId) return Results.NotFound();
            if (transfer.Direction != TransferDirection.Download || transfer.State is not (TransferState.Pending or TransferState.Active))
                return Results.Conflict(new { error = "The transfer is not accepting data." });
            if (chunk.Offset != transfer.BytesTransferred) return Results.Conflict(new { error = "The transfer offset is out of sequence." });

            var newOffset = chunk.Offset + data.Length;
            if (transfer.TotalBytes > 0 && newOffset > transfer.TotalBytes) return Results.BadRequest(new { error = "The transfer exceeds the declared size." });

            var path = database.GetTransferDataPath(chunk.TransferId);
            await using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                stream.Seek(chunk.Offset, SeekOrigin.Begin);
                await stream.WriteAsync(data, ct);
            }

            if (!chunk.Last)
            {
                await database.ReportTransferProgressAsync(transfer.Id, message.DeviceId, newOffset, ct);
                return Results.Ok(new TransferSyncResponse(newOffset, TransferState.Active));
            }

            if (transfer.Sha256 is not null && chunk.Sha256 is not null && !string.Equals(transfer.Sha256, chunk.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await database.FailTransferAsync(transfer.Id, message.DeviceId, "The sent file failed checksum verification.", ct);
                return Results.Conflict(new { error = "The sent file failed checksum verification." });
            }

            await database.CompleteTransferAsync(transfer.Id, message.DeviceId, newOffset, chunk.Sha256, ct);
            return Results.Ok(new TransferSyncResponse(newOffset, TransferState.Completed));
        });

        agent.MapPost("/pull", async (SignedAgentMessage message, AgentMessageVerifier verifier, VeltrixControlStore database, CancellationToken ct) =>
        {
            var request = await verifier.VerifyAsync<TransferChunkPull>(message, ct);
            if (request is null) return Results.Unauthorized();
            if (request.Offset < 0 || request.MaxBytes is < 1 or > FileOperationLimits.MaxTransferChunkBytes)
                return Results.BadRequest(new { error = "The transfer request is invalid." });

            var transfer = await database.GetTransferAsync(request.TransferId, ct);
            if (transfer is null || transfer.DeviceId != message.DeviceId) return Results.NotFound();
            if (transfer.Direction != TransferDirection.Upload || transfer.State is not (TransferState.Pending or TransferState.Active))
                return Results.Conflict(new { error = "The transfer is not providing data." });
            if (request.Offset > transfer.BytesTransferred) return Results.Conflict(new { error = "The transfer offset is out of sequence." });

            var path = database.GetTransferDataPath(transfer.Id);
            var available = transfer.BytesTransferred - request.Offset;
            var remaining = Math.Max(0, transfer.TotalBytes - request.Offset);
            var count = (int)Math.Min(request.MaxBytes, Math.Min(available, remaining));
            var buffer = new byte[count];
            if (count > 0)
            {
                if (!File.Exists(path)) return Results.Conflict(new { error = "The transfer data is not available." });
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(request.Offset, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(buffer, ct);
            }

            var last = request.Offset + count >= transfer.TotalBytes;
            if (last)
            {
                if (transfer.Sha256 is not null && !string.Equals(transfer.Sha256, PathHash(path, transfer.TotalBytes), StringComparison.OrdinalIgnoreCase))
                {
                    await database.FailTransferAsync(transfer.Id, message.DeviceId, "The stored file failed checksum verification.", ct);
                    return Results.Conflict(new { error = "The stored file failed checksum verification." });
                }
                await database.CompleteTransferAsync(transfer.Id, message.DeviceId, transfer.TotalBytes, transfer.Sha256, ct);
                database.TryDeleteTransferFile(transfer.Id);
            }

            return Results.Ok(new TransferChunkResponse(request.Offset, Convert.ToBase64String(buffer), last));
        });

        agent.MapPost("/fail", async (SignedAgentMessage message, AgentMessageVerifier verifier, VeltrixControlStore database, CancellationToken ct) =>
        {
            var request = await verifier.VerifyAsync<TransferFailRequest>(message, ct);
            if (request is null) return Results.Unauthorized();
            await database.FailTransferAsync(request.TransferId, message.DeviceId, request.Error, ct);
            return Results.NoContent();
        });
    }

    public static void MapManagementTransfers(this RouteGroupBuilder management)
    {
        management.MapPost("/devices/{deviceId:guid}/transfers", async (Guid deviceId, TransferRequest request, System.Security.Claims.ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceFiles)) return Results.Forbid();
            var error = InputValidator.Transfer(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var transfer = await database.CreateTransferAsync(deviceId, user.Identity!.Name!, request, ct);
                if (request.Direction == TransferDirection.Upload)
                {
                    var path = database.GetTransferDataPath(transfer.Id);
                    await File.WriteAllBytesAsync(path, [], ct);
                }
                return Results.Accepted($"/api/transfers/{transfer.Id:D}", transfer);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        management.MapGet("/transfers/{transferId:guid}", async (Guid transferId, System.Security.Claims.ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceFiles)) return Results.Forbid();
            var transfer = await database.GetTransferAsync(transferId, ct);
            return transfer is null ? Results.NotFound() : Results.Ok(transfer);
        });

        management.MapGet("/devices/{deviceId:guid}/transfers", async (Guid deviceId, bool? active, int? limit, System.Security.Claims.ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceFiles)) return Results.Forbid();
            return Results.Ok(await database.GetTransfersAsync(deviceId, active ?? false, limit ?? 50, ct));
        });

        management.MapDelete("/transfers/{transferId:guid}", async (Guid transferId, System.Security.Claims.ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceFiles)) return Results.Forbid();
            if (!await database.CancelTransferAsync(transferId, user.Identity!.Name!, ct)) return Results.NotFound();
            database.TryDeleteTransferFile(transferId);
            return Results.NoContent();
        });

        management.MapGet("/transfers/{transferId:guid}/chunks", async (Guid transferId, long? offset, int? count, System.Security.Claims.ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceFiles)) return Results.Forbid();
            var transfer = await database.GetTransferAsync(transferId, ct);
            if (transfer is null) return Results.NotFound();
            if (transfer.Direction != TransferDirection.Download) return Results.BadRequest(new { error = "Only download transfers provide stored data." });
            var start = offset ?? 0;
            if (start < 0 || start > transfer.TotalBytes) return Results.BadRequest(new { error = "The transfer offset is invalid." });
            var length = (int)Math.Min(count ?? 256 * 1024, Math.Min(1024 * 1024, transfer.TotalBytes - start));
            var path = database.GetTransferDataPath(transferId);
            if (length <= 0) return Results.Bytes([], "application/octet-stream");
            if (!File.Exists(path)) return Results.Conflict(new { error = "The transfer data is not available yet." });
            var buffer = new byte[length];
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(start, SeekOrigin.Begin);
                var read = await stream.ReadAtLeastAsync(buffer, length, throwOnEndOfStream: false, ct);
                if (read < length) Array.Resize(ref buffer, read);
            }
            return Results.Bytes(buffer, "application/octet-stream");
        });

        management.MapPut("/transfers/{transferId:guid}/chunks", async (Guid transferId, HttpRequest request, long? offset, System.Security.Claims.ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceFiles)) return Results.Forbid();
            var transfer = await database.GetTransferAsync(transferId, ct);
            if (transfer is null) return Results.NotFound();
            if (transfer.Direction != TransferDirection.Upload || transfer.State is not (TransferState.Pending or TransferState.Active))
                return Results.Conflict(new { error = "The transfer is not accepting data." });
            var start = offset ?? 0;
            if (start != transfer.BytesTransferred) return Results.Conflict(new { error = "The transfer offset is out of sequence." });
            if (request.ContentLength is > FileOperationLimits.MaxTransferChunkBytes) return Results.BadRequest(new { error = "The chunk is too large." });

            using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + read > FileOperationLimits.MaxTransferChunkBytes) return Results.BadRequest(new { error = "The chunk is too large." });
                memory.Write(buffer, 0, read);
            }
            var data = memory.ToArray();
            var newOffset = start + data.Length;
            if (newOffset > transfer.TotalBytes) return Results.BadRequest(new { error = "The transfer exceeds the declared size." });

            var path = database.GetTransferDataPath(transferId);
            await using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Seek(start, SeekOrigin.Begin);
                await stream.WriteAsync(data, ct);
            }

            await database.ReportTransferProgressAsync(transferId, transfer.DeviceId, newOffset, ct);
            return Results.Ok(new TransferSyncResponse(newOffset, TransferState.Active));
        });
    }

    private static string PathHash(string path, long totalBytes)
    {
        if (totalBytes == 0) return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]));
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
