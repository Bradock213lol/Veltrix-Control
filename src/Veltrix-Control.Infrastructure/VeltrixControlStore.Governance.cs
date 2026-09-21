using System.Globalization;
using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<IReadOnlyList<DeviceTagView>> GetDeviceTagsAsync(Guid? deviceId, CancellationToken cancellationToken = default)
    {
        var tags = new List<DeviceTagView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, tag, added_by, added_at FROM device_tags WHERE ($device IS NULL OR device_id = $device) ORDER BY tag COLLATE NOCASE;";
        command.Parameters.AddWithValue("$device", (object?)deviceId?.ToString("D") ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(new DeviceTagView(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3))));
        }
        return tags;
    }

    public async Task<bool> AddDeviceTagAsync(Guid deviceId, string tag, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM device_tags WHERE device_id = $device;";
                count.Parameters.AddWithValue("$device", deviceId.ToString("D"));
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= TagLimits.MaxTagsPerDevice)
                {
                    throw new InvalidOperationException($"A device can carry at most {TagLimits.MaxTagsPerDevice} tags.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO device_tags(device_id, tag, added_by, added_at)
                SELECT $device, $tag, $actor, $now WHERE EXISTS(SELECT 1 FROM devices WHERE id = $device);
                """;
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$tag", tag);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "device.tag.add", deviceId.ToString("D"), "succeeded", $"tag={tag}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveDeviceTagAsync(Guid deviceId, string tag, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM device_tags WHERE device_id = $device AND tag = $tag COLLATE NOCASE;";
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$tag", tag);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await AppendAuditAsync(connection, transaction, actor, "device.tag.remove", deviceId.ToString("D"), "succeeded", $"tag={tag}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetDeviceFavoriteAsync(Guid deviceId, bool favorite, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE devices SET is_favorite = $favorite WHERE id = $device;";
            command.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await AppendAuditAsync(connection, transaction, actor, favorite ? "device.favorite.add" : "device.favorite.remove", deviceId.ToString("D"), "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Enabled, string? WebhookUrl, string? Secret, DateTimeOffset? UpdatedAt)> GetNotificationSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, webhook_url, secret, updated_at FROM notification_settings WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (false, null, null, null);
        return (
            reader.GetInt32(0) == 1,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)));
    }

    public async Task SaveNotificationSettingsAsync(string actor, NotificationSettingsRequest request, string? protectedSecret, CancellationToken cancellationToken = default)
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
                INSERT INTO notification_settings(id, enabled, webhook_url, secret, updated_at)
                VALUES (1, $enabled, $url, $secret, $now)
                ON CONFLICT(id) DO UPDATE SET enabled = $enabled, webhook_url = $url,
                    secret = COALESCE($secret, secret), updated_at = $now;
                """;
            command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$url", (object?)request.WebhookUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$secret", (object?)protectedSecret ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "settings.notifications", "controller", "succeeded",
                $"enabled={request.Enabled} url={request.WebhookUrl}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
