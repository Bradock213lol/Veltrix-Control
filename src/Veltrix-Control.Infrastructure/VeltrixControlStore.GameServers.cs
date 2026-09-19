using System.Globalization;
using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<GameServerView> CreateGameServerAsync(Guid deviceId, string actor, GameServerRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM game_servers WHERE device_id = $device;";
                count.Parameters.AddWithValue("$device", deviceId.ToString("D"));
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= GameServerLimits.MaxServersPerDevice)
                {
                    throw new InvalidOperationException("The device has reached the maximum number of game servers.");
                }
            }

            string deviceName;
            await using (var device = connection.CreateCommand())
            {
                device.Transaction = transaction;
                device.CommandText = "SELECT name FROM devices WHERE id = $device;";
                device.Parameters.AddWithValue("$device", deviceId.ToString("D"));
                deviceName = await device.ExecuteScalarAsync(cancellationToken) as string
                    ?? throw new KeyNotFoundException("Device not found.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO game_servers(id, device_id, name, adapter, state, install_path, port, memory_mb, cpu_threads, auto_restart, environment_json, version, created_by, created_at)
                VALUES ($id, $device, $name, $adapter, 'Provisioning', $path, $port, $memory, $cpu, $auto, $environment, $version, $actor, $now);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name);
            command.Parameters.AddWithValue("$adapter", request.Adapter);
            command.Parameters.AddWithValue("$path", request.InstallPath);
            command.Parameters.AddWithValue("$port", request.Port);
            command.Parameters.AddWithValue("$memory", request.MemoryMb);
            command.Parameters.AddWithValue("$cpu", request.CpuThreads);
            command.Parameters.AddWithValue("$auto", request.AutoRestart ? 1 : 0);
            command.Parameters.AddWithValue("$environment", (object?)request.EnvironmentJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", (object?)request.Version ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "gameserver.create", id.ToString("D"), "provisioning",
                $"device={deviceId:D} adapter={request.Adapter}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GameServerView(id, deviceId, deviceName, request.Name, request.Adapter, "Provisioning", request.InstallPath,
                request.Port, request.MemoryMb, request.CpuThreads, request.AutoRestart, request.Version, request.EnvironmentJson, actor, now, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<GameServerView>> GetGameServersAsync(Guid? deviceId, CancellationToken cancellationToken = default)
    {
        var servers = new List<GameServerView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.id, g.device_id, COALESCE(d.name, ''), g.name, g.adapter, g.state, g.install_path, g.port, g.memory_mb,
                   g.cpu_threads, g.auto_restart, g.version, g.environment_json, g.created_by, g.created_at, g.last_started_at, g.last_stopped_at
            FROM game_servers g LEFT JOIN devices d ON d.id = g.device_id
            WHERE ($device IS NULL OR g.device_id = $device) ORDER BY g.name COLLATE NOCASE LIMIT 200;
            """;
        command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) servers.Add(ReadGameServer(reader));
        return servers;
    }

    public async Task<GameServerView?> GetGameServerAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.id, g.device_id, COALESCE(d.name, ''), g.name, g.adapter, g.state, g.install_path, g.port, g.memory_mb,
                   g.cpu_threads, g.auto_restart, g.version, g.environment_json, g.created_by, g.created_at, g.last_started_at, g.last_stopped_at
            FROM game_servers g LEFT JOIN devices d ON d.id = g.device_id WHERE g.id = $id;
            """;
        command.Parameters.AddWithValue("$id", serverId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGameServer(reader) : null;
    }

    public async Task SetGameServerStateAsync(Guid serverId, string state, bool? started, string? version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE game_servers SET state = $state,
                    version = COALESCE($version, version),
                    last_started_at = CASE WHEN $started = 1 THEN $now ELSE last_started_at END,
                    last_stopped_at = CASE WHEN $started = 0 THEN $now ELSE last_stopped_at END
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
            command.Parameters.AddWithValue("$started", started is null ? DBNull.Value : started.Value ? 1 : 0);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", serverId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordGameServerEventAsync(Guid serverId, string kind, string? data, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO game_server_events(server_id, kind, data, created_at) VALUES ($server, $kind, $data, $now);";
            command.Parameters.AddWithValue("$server", serverId.ToString("D"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<GameServerEventView>> GetGameServerEventsAsync(Guid serverId, int limit, CancellationToken cancellationToken = default)
    {
        var events = new List<GameServerEventView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, server_id, kind, data, created_at FROM game_server_events WHERE server_id = $server ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new GameServerEventView(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), ParseDate(reader.GetString(4))));
        }
        return events;
    }

    public async Task<int> CountRecentGameServerStartsAsync(Guid serverId, int windowMinutes, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM game_server_events WHERE server_id = $server AND kind = 'started' AND created_at > $since;";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        command.Parameters.AddWithValue("$since", Iso(DateTimeOffset.UtcNow.AddMinutes(-windowMinutes)));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static GameServerView ReadGameServer(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10) == 1,
        reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetString(13),
        ParseDate(reader.GetString(14)),
        reader.IsDBNull(15) ? null : ParseDate(reader.GetString(15)),
        reader.IsDBNull(16) ? null : ParseDate(reader.GetString(16)));
}
