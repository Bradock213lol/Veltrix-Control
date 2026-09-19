using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<BackupView> CreateBackupRecordAsync(Guid deviceId, string actor, BackupRequest request, CancellationToken cancellationToken = default)
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
                count.CommandText = "SELECT COUNT(*) FROM backups WHERE device_id = $device;";
                count.Parameters.AddWithValue("$device", deviceId.ToString("D"));
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= MonitoringLimits.MaxBackupsPerDevice)
                {
                    throw new InvalidOperationException("The device has reached the maximum number of recorded backups.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO backups(id, device_id, name, source_path, archive_path, state, created_by, created_at, retention_days)
                SELECT $id, id, $name, $source, $archive, 'Running', $actor, $now, $retention FROM devices WHERE id = $device;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name);
            command.Parameters.AddWithValue("$source", request.SourcePath);
            command.Parameters.AddWithValue("$archive", request.DestinationPath);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            command.Parameters.AddWithValue("$retention", Math.Clamp(request.RetentionDays, 1, 3650));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new KeyNotFoundException("Device not found.");
            }
            await AppendAuditAsync(connection, transaction, actor, "backup.create", id.ToString("D"), "queued",
                $"device={deviceId:D} source={request.SourcePath}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BackupView(id, deviceId, request.Name, request.SourcePath, request.DestinationPath, "Running", 0, null, null, actor, now, null, request.RetentionDays);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteBackupAsync(Guid backupId, bool succeeded, long sizeBytes, string? sha256, string? error, CancellationToken cancellationToken = default)
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
                UPDATE backups SET state = $state, size_bytes = $size, sha256 = $sha, error = $error, completed_at = $now
                WHERE id = $id AND state = 'Running';
                """;
            command.Parameters.AddWithValue("$state", succeeded ? "Completed" : "Failed");
            command.Parameters.AddWithValue("$size", sizeBytes);
            command.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", backupId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await AppendAuditAsync(connection, transaction, "agent", "backup.complete", backupId.ToString("D"), succeeded ? "succeeded" : "failed", error, cancellationToken);
            }

            if (succeeded)
            {
                await using var expired = connection.CreateCommand();
                expired.Transaction = transaction;
                expired.CommandText = """
                    SELECT b.id, b.archive_path, b.device_id FROM backups b
                    WHERE b.state = 'Completed'
                      AND b.completed_at IS NOT NULL
                      AND datetime(b.completed_at) < datetime('now', '-' || b.retention_days || ' days')
                      AND NOT EXISTS (SELECT 1 FROM operations o WHERE o.argument LIKE '%' || b.archive_path || '%' AND o.state IN ('Queued','Running'))
                    LIMIT 10;
                    """;
                var expiredBackups = new List<(string Id, string Archive, string Device)>();
                await using (var reader = await expired.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken)) expiredBackups.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
                foreach (var (expiredId, archive, device) in expiredBackups)
                {
                    await using var mark = connection.CreateCommand();
                    mark.Transaction = transaction;
                    mark.CommandText = "UPDATE backups SET state = 'Expired' WHERE id = $id AND state = 'Completed';";
                    mark.Parameters.AddWithValue("$id", expiredId);
                    await mark.ExecuteNonQueryAsync(cancellationToken);

                    await using var operation = connection.CreateCommand();
                    operation.Transaction = transaction;
                    operation.CommandText = """
                        INSERT INTO operations(id, device_id, kind, state, argument, requested_by, requested_at)
                        VALUES ($id, $device, 'DeleteFile', 'Queued', $argument, 'retention', $now);
                        """;
                    operation.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                    operation.Parameters.AddWithValue("$device", device);
                    operation.Parameters.AddWithValue("$argument", archive);
                    operation.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
                    await operation.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackupView?> GetBackupAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, device_id, name, source_path, archive_path, state, size_bytes, sha256, error, created_by, created_at, completed_at, retention_days FROM backups WHERE id = $id;";
        command.Parameters.AddWithValue("$id", backupId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBackup(reader) : null;
    }

    public async Task<IReadOnlyList<BackupView>> GetBackupsAsync(Guid? deviceId, int limit, CancellationToken cancellationToken = default)
    {
        var backups = new List<BackupView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, name, source_path, archive_path, state, size_bytes, sha256, error, created_by, created_at, completed_at, retention_days
            FROM backups WHERE ($device IS NULL OR device_id = $device) ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) backups.Add(ReadBackup(reader));
        return backups;
    }

    public async Task<Guid> RaiseAlertAsync(Guid? deviceId, string severity, string code, string title, string message, string? metadata, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT id FROM alerts WHERE state = 'Open' AND code = $code AND COALESCE(device_id, '') = $device LIMIT 1;";
                existing.Parameters.AddWithValue("$code", code);
                existing.Parameters.AddWithValue("$device", deviceId?.ToString("D") ?? string.Empty);
                if (await existing.ExecuteScalarAsync(cancellationToken) is string openId)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return Guid.Parse(openId);
                }
            }

            var id = Guid.NewGuid();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO alerts(id, device_id, severity, code, title, message, state, created_at, metadata)
                VALUES ($id, $device, $severity, $code, $title, $message, 'Open', $now, $metadata);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$severity", severity);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$metadata", (object?)metadata ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, "monitor", "alert.raise", id.ToString("D"), "open", $"code={code} severity={severity}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResolveAlertAsync(Guid deviceId, string code, string actor, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE alerts SET state = 'Resolved', acknowledged_by = $actor, acknowledged_at = $now WHERE state = 'Open' AND code = $code AND device_id = $device;";
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlertView>> GetAlertsAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var alerts = new List<AlertView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, severity, code, title, message, state, created_at, acknowledged_by, acknowledged_at, metadata
            FROM alerts WHERE ($state IS NULL OR state = $state) ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MonitoringLimits.MaxAlertsReturned));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(new AlertView(
                Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                ParseDate(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return alerts;
    }

    public async Task<IReadOnlyList<AlertView>> GetAlertsCreatedAfterAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var alerts = new List<AlertView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, severity, code, title, message, state, created_at, acknowledged_by, acknowledged_at, metadata
            FROM alerts WHERE created_at > $since ORDER BY created_at LIMIT 200;
            """;
        command.Parameters.AddWithValue("$since", Iso(since));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(new AlertView(
                Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                ParseDate(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return alerts;
    }

    public async Task<bool> AcknowledgeAlertAsync(Guid alertId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE alerts SET state = 'Acknowledged', acknowledged_by = $actor, acknowledged_at = $now WHERE id = $id AND state = 'Open';";
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", alertId.ToString("D"));
            var acknowledged = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (acknowledged)
            {
                await AppendAuditAsync(connection, transaction, actor, "alert.acknowledge", alertId.ToString("D"), "succeeded", null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return acknowledged;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AutoResolveAlertAsync(Guid deviceId, string code, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE alerts SET state = 'Resolved', acknowledged_at = $now, acknowledged_by = 'monitor' WHERE state = 'Open' AND code = $code AND device_id = $device;";
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AutomationView> CreateAutomationAsync(string actor, AutomationRequest request, CancellationToken cancellationToken = default)
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
                count.CommandText = "SELECT COUNT(*) FROM automations;";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= MonitoringLimits.MaxAutomations)
                {
                    throw new InvalidOperationException("The automation limit has been reached.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO automations(id, name, enabled, trigger_json, condition_json, action_json, cooldown_seconds, created_by, created_at)
                VALUES ($id, $name, $enabled, $trigger, $condition, $action, $cooldown, $actor, $now);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name);
            command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$trigger", JsonSerializer.Serialize(request.Trigger, JsonOptions));
            command.Parameters.AddWithValue("$condition", (object?)JsonSerializer.Serialize(request.Condition, JsonOptions) ?? DBNull.Value);
            command.Parameters.AddWithValue("$action", JsonSerializer.Serialize(request.Action, JsonOptions));
            command.Parameters.AddWithValue("$cooldown", Math.Clamp(request.CooldownSeconds, 30, MonitoringLimits.MaxCooldownSeconds));
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "automation.create", id.ToString("D"), "succeeded", $"name={request.Name}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AutomationView(id, request.Name, request.Enabled, JsonSerializer.Serialize(request.Trigger, JsonOptions),
                JsonSerializer.Serialize(request.Condition, JsonOptions), JsonSerializer.Serialize(request.Action, JsonOptions),
                request.CooldownSeconds, actor, now, null, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AutomationView>> GetAutomationsAsync(bool? enabled, CancellationToken cancellationToken = default)
    {
        var automations = new List<AutomationView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, enabled, trigger_json, condition_json, action_json, cooldown_seconds, created_by, created_at, last_run_at, run_count
            FROM automations WHERE ($enabled IS NULL OR enabled = $enabled) ORDER BY name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$enabled", enabled is null ? DBNull.Value : enabled.Value ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            automations.Add(new AutomationView(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2) == 1,
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetString(7), ParseDate(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)), reader.GetInt32(10)));
        }
        return automations;
    }

    public async Task<bool> SetAutomationEnabledAsync(Guid automationId, bool enabled, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE automations SET enabled = $enabled WHERE id = $id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$id", automationId.ToString("D"));
            var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (updated)
            {
                await AppendAuditAsync(connection, transaction, actor, enabled ? "automation.enable" : "automation.disable", automationId.ToString("D"), "succeeded", null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAutomationAsync(Guid automationId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM automations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", automationId.ToString("D"));
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (deleted)
            {
                await AppendAuditAsync(connection, transaction, actor, "automation.delete", automationId.ToString("D"), "succeeded", null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAutomationRunAsync(Guid automationId, Guid? deviceId, string state, string? detail, CancellationToken cancellationToken = default)
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
                INSERT INTO automation_runs(id, automation_id, device_id, state, detail, started_at, finished_at)
                VALUES ($id, $automation, $device, $state, $detail, $now, $now);
                UPDATE automations SET last_run_at = $now, run_count = run_count + 1 WHERE id = $automation;
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$automation", automationId.ToString("D"));
            command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AutomationRunView>> GetAutomationRunsAsync(Guid? automationId, int limit, CancellationToken cancellationToken = default)
    {
        var runs = new List<AutomationRunView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, automation_id, device_id, state, detail, started_at, finished_at
            FROM automation_runs WHERE ($automation IS NULL OR automation_id = $automation)
            ORDER BY started_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$automation", (object?)automationId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new AutomationRunView(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseDate(reader.GetString(5)), reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6))));
        }
        return runs;
    }

    private static BackupView ReadBackup(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetString(9), ParseDate(reader.GetString(10)), reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)), reader.GetInt32(12));
}
