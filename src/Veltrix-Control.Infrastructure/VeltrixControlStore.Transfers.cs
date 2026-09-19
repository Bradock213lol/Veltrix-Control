using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    private string TransferDirectory => Path.Combine(Path.GetDirectoryName(_databasePath) ?? ".", "transfers");

    public string GetTransferDataPath(Guid transferId)
    {
        Directory.CreateDirectory(TransferDirectory);
        return Path.Combine(TransferDirectory, $"{transferId:D}.part");
    }

    public async Task<TransferView> CreateTransferAsync(Guid deviceId, string actor, TransferRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var transfer = new TransferView(
            Guid.NewGuid(), deviceId, request.Direction, TransferState.Pending, request.Path,
            0, 0, request.Sha256, null, actor, now, null);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var expired = new List<Guid>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id FROM transfers WHERE state IN ('Completed','Failed','Cancelled') AND created_at < $cutoff;";
                select.Parameters.AddWithValue("$cutoff", Iso(now.AddDays(-2)));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) expired.Add(Guid.Parse(reader.GetString(0)));
            }
            if (expired.Count > 0)
            {
                await using var cleanup = connection.CreateCommand();
                cleanup.Transaction = transaction;
                cleanup.CommandText = "DELETE FROM transfers WHERE state IN ('Completed','Failed','Cancelled') AND created_at < $cutoff;";
                cleanup.Parameters.AddWithValue("$cutoff", Iso(now.AddDays(-2)));
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO transfers(id, device_id, direction, state, path, total_bytes, bytes_transferred, sha256, requested_by, created_at)
                SELECT $id, id, $direction, 'Pending', $path, $total, 0, $sha256, $actor, $created
                FROM devices WHERE id = $device;
                """;
            command.Parameters.AddWithValue("$id", transfer.Id.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$direction", request.Direction.ToString());
            command.Parameters.AddWithValue("$path", request.Path);
            command.Parameters.AddWithValue("$total", request.TotalBytes);
            command.Parameters.AddWithValue("$sha256", (object?)request.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$created", Iso(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new KeyNotFoundException("Device not found.");
            }
            await AppendAuditAsync(connection, transaction, actor, "device.transfer.create", deviceId.ToString("D"), "queued",
                $"transfer={transfer.Id:D} direction={request.Direction} path={request.Path}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (var expiredId in expired) TryDeleteTransferFile(expiredId);
            return transfer with { TotalBytes = request.TotalBytes };
        }
        catch
        {
            TryDeleteTransferFile(transfer.Id);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransferView?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, device_id, direction, state, path, total_bytes, bytes_transferred, sha256, error, requested_by, created_at, completed_at FROM transfers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", transferId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTransfer(reader) : null;
    }

    public async Task<IReadOnlyList<TransferView>> GetTransfersAsync(Guid? deviceId, bool activeOnly, int limit, CancellationToken cancellationToken = default)
    {
        var transfers = new List<TransferView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, device_id, direction, state, path, total_bytes, bytes_transferred, sha256, error, requested_by, created_at, completed_at
            FROM transfers
            WHERE ($device IS NULL OR device_id = $device) AND ($active = 0 OR state IN ('Pending','Active'))
            ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", activeOnly ? 1 : 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transfers.Add(ReadTransfer(reader));
        }
        return transfers;
    }

    public async Task<IReadOnlyList<AgentPendingTransfer>> GetPendingAgentTransfersAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var transfers = new List<AgentPendingTransfer>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, direction, path, total_bytes, bytes_transferred, sha256 FROM transfers
            WHERE device_id = $device AND state IN ('Pending','Active') ORDER BY created_at LIMIT 10;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transfers.Add(new AgentPendingTransfer(
                Guid.Parse(reader.GetString(0)),
                Enum.Parse<TransferDirection>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return transfers;
    }

    public async Task<bool> ReportTransferProgressAsync(Guid transferId, Guid deviceId, long offset, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transfers SET bytes_transferred = $offset, state = 'Active'
            WHERE id = $id AND device_id = $device AND state IN ('Pending','Active') AND $offset >= bytes_transferred;
            """;
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$id", transferId.ToString("D"));
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteTransferAsync(Guid transferId, Guid deviceId, long totalBytes, string? sha256, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE transfers SET state = 'Completed', total_bytes = $total, bytes_transferred = $total,
                    sha256 = COALESCE($sha, sha256), completed_at = $now
                WHERE id = $id AND device_id = $device AND state IN ('Pending','Active');
                """;
            command.Parameters.AddWithValue("$total", totalBytes);
            command.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", transferId.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Transfer is not active.");
            }
            await AppendAuditAsync(connection, transaction, $"device:{deviceId:D}", "device.transfer.complete", transferId.ToString("D"), "succeeded", $"bytes={totalBytes}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FailTransferAsync(Guid transferId, Guid deviceId, string error, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transfers SET state = 'Failed', error = $error, completed_at = $now WHERE id = $id AND device_id = $device AND state IN ('Pending','Active');";
        command.Parameters.AddWithValue("$error", error.Length > 2048 ? error[..2048] : error);
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", transferId.ToString("D"));
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CancelTransferAsync(Guid transferId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE transfers SET state = 'Cancelled', completed_at = $now WHERE id = $id AND state IN ('Pending','Active');";
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", transferId.ToString("D"));
            var cancelled = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (cancelled)
            {
                await AppendAuditAsync(connection, transaction, actor, "device.transfer.cancel", transferId.ToString("D"), "succeeded", null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return cancelled;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void TryDeleteTransferFile(Guid transferId)
    {
        try
        {
            var path = Path.Combine(TransferDirectory, $"{transferId:D}.part");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The part file is removed on the next cleanup pass.
        }
        catch (UnauthorizedAccessException)
        {
            // The part file is removed on the next cleanup pass.
        }
    }

    private static TransferView ReadTransfer(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Enum.Parse<TransferDirection>(reader.GetString(2)),
        Enum.Parse<TransferState>(reader.GetString(3)),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetString(9),
        ParseDate(reader.GetString(10)),
        reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)));
}
