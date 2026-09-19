using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<TerminalSessionView> CreateTerminalSessionAsync(Guid deviceId, string actor, TerminalShell shell, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new TerminalSessionView(Guid.NewGuid(), deviceId, shell.ToString(), workingDirectory, "Active", actor, now, now);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO terminal_sessions(id, device_id, shell, working_directory, state, created_by, created_at, last_activity)
                SELECT $id, id, $shell, $cwd, 'Active', $actor, $now, $now FROM devices WHERE id = $device;
                """;
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$shell", shell.ToString());
            command.Parameters.AddWithValue("$cwd", workingDirectory);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new KeyNotFoundException("Device not found.");
            }
            await AppendAuditAsync(connection, transaction, actor, "device.terminal.start", deviceId.ToString("D"), "queued",
                $"session={session.Id:D} shell={shell}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TerminalSessionView>> GetTerminalSessionsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken = default)
    {
        var sessions = new List<TerminalSessionView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, shell, working_directory, state, created_by, created_at, last_activity
            FROM terminal_sessions WHERE ($device IS NULL OR device_id = $device)
            ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(ReadTerminalSession(reader));
        }
        return sessions;
    }

    public async Task<TerminalSessionView?> GetTerminalSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, device_id, shell, working_directory, state, created_by, created_at, last_activity FROM terminal_sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTerminalSession(reader) : null;
    }

    public async Task CloseTerminalSessionAsync(Guid sessionId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE terminal_sessions SET state = 'Closed', last_activity = $now WHERE id = $id AND state = 'Active';";
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await AppendAuditAsync(connection, transaction, actor, "device.terminal.stop", sessionId.ToString("D"), "succeeded", null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountActiveTerminalSessionsAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM terminal_sessions WHERE device_id = $device AND state = 'Active';";
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<HardwareInventory?> GetDeviceInventoryAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT inventory_json FROM devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<HardwareInventory>(json, JsonOptions);
    }

    private static TerminalSessionView ReadTerminalSession(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        ParseDate(reader.GetString(6)),
        ParseDate(reader.GetString(7)));
}
