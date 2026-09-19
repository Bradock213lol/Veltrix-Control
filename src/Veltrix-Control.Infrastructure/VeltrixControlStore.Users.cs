using System.Globalization;
using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Security;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<bool> HasOwnerAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM users WHERE role = 'Owner' LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> EnsureRecoveryAccountAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (await ValidateUsernameAsync(username, cancellationToken) is not null) return false;
        var created = await CreateUserAsync("recovery", new CreateUserRequest(username, password, "Administrator"), PasswordHasher.Hash(password), cancellationToken);
        return created is not null;
    }

    public async Task<IReadOnlyList<UserView>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = new List<UserView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, role, created_at FROM users ORDER BY username COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new UserView(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3))));
        }
        return users;
    }

    public async Task<(Guid Id, string Username, string Role)?> ValidateUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, role FROM users WHERE username = $username;";
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2));
    }

    public async Task<IReadOnlyList<string>> GetUsernamesAsync(CancellationToken cancellationToken = default)
    {
        var usernames = new List<string>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT username FROM users ORDER BY username COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) usernames.Add(reader.GetString(0));
        return usernames;
    }

    public async Task<UserView?> CreateUserAsync(string actor, CreateUserRequest request, string passwordHash, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT COUNT(*) FROM users WHERE username = $username;";
                existing.Parameters.AddWithValue("$username", request.Username.Trim());
                if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
                {
                    return null;
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO users(id, username, password_hash, role, created_at) VALUES ($id, $username, $hash, $role, $now);";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$username", request.Username.Trim());
            command.Parameters.AddWithValue("$hash", passwordHash);
            command.Parameters.AddWithValue("$role", UserRoles.Canonical(request.Role));
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "user.create", id.ToString("D"), "succeeded", $"username={request.Username} role={request.Role}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UserView(id, request.Username.Trim(), UserRoles.Canonical(request.Role), now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateUserRoleAsync(Guid userId, string role, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (!await CanDemoteOwnerAsync(connection, transaction, userId, role, cancellationToken))
            {
                throw new InvalidOperationException("The last Owner account cannot be demoted.");
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE users SET role = $role WHERE id = $id;";
            command.Parameters.AddWithValue("$role", UserRoles.Canonical(role));
            command.Parameters.AddWithValue("$id", userId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await AppendAuditAsync(connection, transaction, actor, "user.role", userId.ToString("D"), "succeeded", $"role={role}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ResetUserPasswordAsync(Guid userId, string passwordHash, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id;";
            command.Parameters.AddWithValue("$hash", passwordHash);
            command.Parameters.AddWithValue("$id", userId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await AppendAuditAsync(connection, transaction, actor, "user.password", userId.ToString("D"), "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteUserAsync(Guid userId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (!await CanDemoteOwnerAsync(connection, transaction, userId, "Delete", cancellationToken))
            {
                throw new InvalidOperationException("The last Owner account cannot be deleted.");
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM users WHERE id = $id;";
            command.Parameters.AddWithValue("$id", userId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await AppendAuditAsync(connection, transaction, actor, "user.delete", userId.ToString("D"), "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PurgeOlderThanAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoff = Iso(DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650)));
        var closedCutoff = Iso(DateTimeOffset.UtcNow.AddDays(-7));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM operations WHERE finished_at IS NOT NULL AND finished_at < $cutoff;
                DELETE FROM compute_jobs WHERE finished_at IS NOT NULL AND finished_at < $cutoff;
                DELETE FROM automation_runs WHERE started_at < $cutoff;
                DELETE FROM terminal_events WHERE created_at < $closedCutoff;
                DELETE FROM terminal_sessions WHERE state = 'Closed' AND last_activity < $closedCutoff;
                DELETE FROM alerts WHERE state IN ('Resolved','Acknowledged') AND created_at < $cutoff;
                DELETE FROM windows_update_scans WHERE scanned_at < $cutoff;
                DELETE FROM game_server_events WHERE created_at < $cutoff;
                DELETE FROM metrics WHERE captured_at < $cutoff;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.Parameters.AddWithValue("$closedCutoff", closedCutoff);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(int Devices, int OnlineDevices, int OpenAlerts, int QueuedOperations)> GetSystemCountsAsync(TimeSpan onlineWindow, CancellationToken cancellationToken = default)
    {
        var devices = await GetDevicesAsync(onlineWindow, cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM alerts WHERE state = 'Open'),
                   (SELECT COUNT(*) FROM operations WHERE state IN ('Queued','Running'));
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (devices.Count, devices.Count(item => item.Online), 0, 0);
        return (devices.Count, devices.Count(item => item.Online), reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<bool> CanDemoteOwnerAsync(SqliteConnection connection, SqliteTransaction transaction, Guid userId, string newRole, CancellationToken cancellationToken)
    {
        if (string.Equals(newRole, "Owner", StringComparison.OrdinalIgnoreCase)) return true;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT role FROM users WHERE id = $id;";
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        if (await command.ExecuteScalarAsync(cancellationToken) as string is not "Owner") return true;

        await using var owners = connection.CreateCommand();
        owners.Transaction = transaction;
        owners.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'Owner';";
        return Convert.ToInt64(await owners.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 1;
    }
}
