using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VeltrixControl.Core.Health;
using VeltrixControl.Core.Security;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VeltrixControlStore(IOptions<StoreOptions> options)
    {
        _databasePath = Path.GetFullPath(options.Value.DatabasePath);
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM users LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> CreateOwnerAsync(string username, string passwordHash, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM users;";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            {
                return false;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO users(id, username, password_hash, role, created_at) VALUES ($id, $username, $hash, 'Owner', $now);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$username", username.Trim());
            command.Parameters.AddWithValue("$hash", passwordHash);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, username, "admin.bootstrap", "controller", "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(string Username, string Role)?> ValidateUserAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT username, password_hash, role FROM users WHERE username = $username;";
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !PasswordHasher.Verify(password, reader.GetString(1)))
        {
            return null;
        }
        return (reader.GetString(0), reader.GetString(2));
    }

    public async Task<EnrollmentTokenResponse> CreateEnrollmentTokenAsync(string actor, int lifetimeMinutes, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var code = SecretGenerator.CreateEnrollmentCode();
        var expiresAt = now.AddMinutes(lifetimeMinutes);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO enrollment_tokens(id, token_hash, expires_at, created_by, created_at)
                VALUES ($id, $hash, $expires, $actor, $now);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$hash", SecretGenerator.HashToken(code));
            command.Parameters.AddWithValue("$expires", Iso(expiresAt));
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "enrollment.token.create", id.ToString("D"), "succeeded", $"expires={Iso(expiresAt)}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        return new EnrollmentTokenResponse(id, code, expiresAt);
    }

    public async Task ImportBootstrapEnrollmentCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (code.Length is < 16 or > 64) throw new InvalidDataException("The bootstrap enrollment code is invalid.");
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var expiresAt = now.AddMinutes(15);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO enrollment_tokens(id, token_hash, expires_at, created_by, created_at)
                VALUES ($id, $hash, $expires, 'local-installer', $now);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$hash", SecretGenerator.HashToken(code.Trim().ToUpperInvariant()));
            command.Parameters.AddWithValue("$expires", Iso(expiresAt));
            command.Parameters.AddWithValue("$now", Iso(now));
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
            if (inserted == 1)
            {
                await AppendAuditAsync(connection, transaction, "local-installer", "enrollment.token.bootstrap", id.ToString("D"), "succeeded", $"expires={Iso(expiresAt)}", cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RevokeEnrollmentTokenAsync(Guid tokenId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE enrollment_tokens SET revoked_at = $now WHERE id = $id AND used_at IS NULL AND revoked_at IS NULL;";
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", tokenId.ToString("D"));
            var revoked = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (revoked)
            {
                await AppendAuditAsync(connection, transaction, actor, "enrollment.token.revoke", tokenId.ToString("D"), "succeeded", null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return revoked;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EnrollmentResponse?> EnrollDeviceAsync(EnrollmentRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var tokenHash = SecretGenerator.HashToken(request.Code.Trim().ToUpperInvariant());

            await using var token = connection.CreateCommand();
            token.Transaction = transaction;
            token.CommandText = """
                SELECT id FROM enrollment_tokens
                WHERE token_hash = $hash AND used_at IS NULL AND revoked_at IS NULL AND expires_at > $now;
                """;
            token.Parameters.AddWithValue("$hash", tokenHash);
            token.Parameters.AddWithValue("$now", Iso(now));
            var tokenId = await token.ExecuteScalarAsync(cancellationToken) as string;
            if (tokenId is null)
            {
                return null;
            }

            ValidatePublicKey(request.DevicePublicKey);
            var deviceId = Guid.NewGuid();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO devices(id, name, public_key, inventory_json, enrolled_at, last_heartbeat, agent_version, is_simulation)
                VALUES ($id, $name, $key, $inventory, $now, $now, $version, $simulation);
                UPDATE enrollment_tokens SET used_at = $now WHERE id = $tokenId AND used_at IS NULL;
                """;
            insert.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            insert.Parameters.AddWithValue("$name", request.DeviceName.Trim());
            insert.Parameters.AddWithValue("$key", request.DevicePublicKey);
            insert.Parameters.AddWithValue("$inventory", JsonSerializer.Serialize(request.Inventory, JsonOptions));
            insert.Parameters.AddWithValue("$now", Iso(now));
            insert.Parameters.AddWithValue("$version", request.Inventory.AgentVersion);
            insert.Parameters.AddWithValue("$simulation", request.Inventory.IsSimulation ? 1 : 0);
            insert.Parameters.AddWithValue("$tokenId", tokenId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, "enrollment", "device.enroll", deviceId.ToString("D"), "succeeded", $"name={request.DeviceName}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EnrollmentResponse(deviceId, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetDevicePublicKeyAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT public_key FROM devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<bool> TryUseNonceAsync(Guid deviceId, string nonce, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM used_nonces WHERE used_at < $cutoff;
                INSERT INTO used_nonces(device_id, nonce, used_at) VALUES ($device, $nonce, $now);
                """;
            command.Parameters.AddWithValue("$cutoff", Iso(now.AddMinutes(-10)));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$nonce", nonce);
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task RecordHeartbeatAsync(Guid deviceId, HeartbeatPayload payload, CancellationToken cancellationToken = default)
    {
        var inventory = JsonSerializer.Serialize(payload.Inventory, JsonOptions);
        var telemetry = JsonSerializer.Serialize(payload.Telemetry, JsonOptions);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET name = $name, inventory_json = $inventory, telemetry_json = $telemetry,
                last_heartbeat = $heartbeat, agent_version = $version WHERE id = $id;
            INSERT INTO metrics(device_id, captured_at, cpu_percent, used_memory_bytes, total_memory_bytes, payload_json)
                VALUES ($id, $captured, $cpu, $used, $total, $telemetry);
            DELETE FROM metrics WHERE captured_at < $retention;
            """;
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$name", payload.DeviceName);
        command.Parameters.AddWithValue("$inventory", inventory);
        command.Parameters.AddWithValue("$telemetry", telemetry);
        command.Parameters.AddWithValue("$heartbeat", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$version", payload.Inventory.AgentVersion);
        command.Parameters.AddWithValue("$captured", Iso(payload.Telemetry.CapturedAt));
        command.Parameters.AddWithValue("$cpu", payload.Telemetry.CpuPercent);
        command.Parameters.AddWithValue("$used", payload.Telemetry.UsedMemoryBytes);
        command.Parameters.AddWithValue("$total", payload.Telemetry.TotalMemoryBytes);
        command.Parameters.AddWithValue("$retention", Iso(DateTimeOffset.UtcNow.AddDays(-30)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(TimeSpan onlineWindow, CancellationToken cancellationToken = default)
    {
        var devices = new List<DeviceSummary>();
        var tags = new Dictionary<Guid, List<string>>();
        await using (var tagConnection = OpenConnection())
        {
            await tagConnection.OpenAsync(cancellationToken);
            await using var tagCommand = tagConnection.CreateCommand();
            tagCommand.CommandText = "SELECT device_id, tag FROM device_tags ORDER BY tag COLLATE NOCASE;";
            await using var tagReader = await tagCommand.ExecuteReaderAsync(cancellationToken);
            while (await tagReader.ReadAsync(cancellationToken))
            {
                var tagDevice = Guid.Parse(tagReader.GetString(0));
                if (!tags.TryGetValue(tagDevice, out var list))
                {
                    list = [];
                    tags[tagDevice] = list;
                }
                list.Add(tagReader.GetString(1));
            }
        }

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, last_heartbeat, inventory_json, telemetry_json, is_favorite FROM devices ORDER BY name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            var heartbeat = ParseDate(reader.GetString(2));
            var inventory = JsonSerializer.Deserialize<HardwareInventory>(reader.GetString(3), JsonOptions)!;
            var telemetry = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<TelemetrySnapshot>(reader.GetString(4), JsonOptions);
            var online = DateTimeOffset.UtcNow - heartbeat <= onlineWindow;
            devices.Add(new DeviceSummary(id, reader.GetString(1), online, heartbeat, inventory, telemetry, HealthScorer.Calculate(telemetry, online))
            {
                IsFavorite = reader.GetInt32(5) == 1,
                Tags = tags.TryGetValue(id, out var list) ? list : null
            });
        }
        return devices;
    }

    public async Task<OperationView> CreateOperationAsync(Guid deviceId, string actor, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var operation = new OperationView(Guid.NewGuid(), deviceId, request.Kind, OperationState.Queued, request.Argument, null, null, DateTimeOffset.UtcNow, null, null);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO operations(id, device_id, kind, state, argument, requested_by, requested_at)
                SELECT $id, id, $kind, $state, $argument, $actor, $requested FROM devices WHERE id = $device;
                """;
            command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$kind", request.Kind.ToString());
            command.Parameters.AddWithValue("$state", operation.State.ToString());
            command.Parameters.AddWithValue("$argument", (object?)request.Argument ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$requested", Iso(operation.RequestedAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new KeyNotFoundException("Device not found.");
            }
            await AppendAuditAsync(connection, transaction, actor, $"device.{request.Kind.ToString().ToLowerInvariant()}", deviceId.ToString("D"), "queued", $"operation={operation.Id:D}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return operation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OperationAssignment>> ClaimPendingOperationsAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var operations = new List<OperationAssignment>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, kind, argument, requested_at FROM operations
                WHERE device_id = $device AND state = 'Queued' ORDER BY requested_at LIMIT 20;
                """;
            select.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    operations.Add(new OperationAssignment(
                        Guid.Parse(reader.GetString(0)),
                        Enum.Parse<OperationKind>(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        ParseDate(reader.GetString(3))));
                }
            }

            foreach (var operation in operations)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE operations SET state = 'Running', started_at = $now WHERE id = $id AND state = 'Queued';";
                update.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return operations;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteOperationAsync(Guid deviceId, OperationResultPayload result, CancellationToken cancellationToken = default)
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
                UPDATE operations SET state = $state, result_json = $result, error = $error, finished_at = $finished
                WHERE id = $id AND device_id = $device AND state = 'Running';
                """;
            command.Parameters.AddWithValue("$state", result.State.ToString());
            command.Parameters.AddWithValue("$result", (object?)result.ResultJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$finished", Iso(result.FinishedAt));
            command.Parameters.AddWithValue("$id", result.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Operation does not exist or is not running.");
            }
            await ApplyOperationToDeploymentAsync(connection, transaction, result.OperationId, result.State, result.Error, result.FinishedAt, cancellationToken);
            await AppendAuditAsync(connection, transaction, $"device:{deviceId:D}", "operation.complete", result.OperationId.ToString("D"), result.State.ToString().ToLowerInvariant(), result.Error, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationView?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, device_id, kind, state, argument, result_json, error, requested_at, started_at, finished_at FROM operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadOperation(reader);
    }

    public async Task<IReadOnlyList<AuditEventView>> GetAuditEventsAsync(int limit, CancellationToken cancellationToken = default)
    {
        var events = new List<AuditEventView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, timestamp, actor, action, target, outcome, metadata FROM audit_events ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AuditEventView(reader.GetInt64(0), ParseDate(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return events;
    }

    public async Task RecordAuditEventAsync(string actor, string action, string target, string outcome, string? metadata, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, action, target, outcome, metadata, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> VerifyAuditChainAsync(CancellationToken cancellationToken = default)
    {
        var previous = "GENESIS";
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT timestamp, actor, action, target, outcome, metadata, previous_hash, event_hash FROM audit_events ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(previous), Encoding.UTF8.GetBytes(reader.GetString(6)))) return false;
            var expected = AuditHash(previous, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(reader.GetString(7)))) return false;
            previous = expected;
        }
        return true;
    }

    private static async Task AppendAuditAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string actor, string action, string target, string outcome, string? metadata, CancellationToken cancellationToken)
    {
        await using var previousCommand = connection.CreateCommand();
        previousCommand.Transaction = (SqliteTransaction)transaction;
        previousCommand.CommandText = "SELECT event_hash FROM audit_events ORDER BY id DESC LIMIT 1;";
        var previous = await previousCommand.ExecuteScalarAsync(cancellationToken) as string ?? "GENESIS";
        var timestamp = Iso(DateTimeOffset.UtcNow);
        var hash = AuditHash(previous, timestamp, actor, action, target, outcome, metadata);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO audit_events(timestamp, actor, action, target, outcome, metadata, previous_hash, event_hash) VALUES ($timestamp, $actor, $action, $target, $outcome, $metadata, $previous, $hash);";
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$metadata", (object?)metadata ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous", previous);
        command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string AuditHash(string previous, string timestamp, string actor, string action, string target, string outcome, string? metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{previous}\n{timestamp}\n{actor}\n{action}\n{target}\n{outcome}\n{metadata}")));

    private static OperationView ReadOperation(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
        Enum.Parse<OperationKind>(reader.GetString(2)), Enum.Parse<OperationState>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), ParseDate(reader.GetString(7)),
        reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)), reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)));

    private static void ValidatePublicKey(string publicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (key.KeySize < 256) throw new CryptographicException("Device key is too small.");
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public void Dispose() => _gate.Dispose();
}
