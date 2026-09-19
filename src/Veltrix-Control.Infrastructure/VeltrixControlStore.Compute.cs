using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<ComputePolicyView> UpsertComputePolicyAsync(Guid deviceId, string actor, ComputePolicyRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO compute_policies(device_id, mode, reserved_cpu_threads, reserved_memory_bytes, reserved_disk_bytes, updated_at)
                SELECT id, $mode, $cpu, $memory, $disk, $now FROM devices WHERE id = $device
                ON CONFLICT(device_id) DO UPDATE SET
                    mode = $mode, reserved_cpu_threads = $cpu, reserved_memory_bytes = $memory,
                    reserved_disk_bytes = $disk, updated_at = $now;
                """;
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$mode", request.Mode);
            command.Parameters.AddWithValue("$cpu", request.ReservedCpuThreads);
            command.Parameters.AddWithValue("$memory", request.ReservedMemoryBytes);
            command.Parameters.AddWithValue("$disk", request.ReservedDiskBytes);
            command.Parameters.AddWithValue("$now", Iso(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new KeyNotFoundException("Device not found.");
            }
            await AppendAuditAsync(connection, transaction, actor, "compute.policy.update", deviceId.ToString("D"), "succeeded",
                $"mode={request.Mode} cpu={request.ReservedCpuThreads}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ComputePolicyView(deviceId, request.Mode, request.ReservedCpuThreads, request.ReservedMemoryBytes, request.ReservedDiskBytes, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ComputePolicyView?> GetComputePolicyAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, mode, reserved_cpu_threads, reserved_memory_bytes, reserved_disk_bytes, updated_at FROM compute_policies WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPolicy(reader) : null;
    }

    public async Task<IReadOnlyList<ComputePolicyView>> GetComputePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policies = new List<ComputePolicyView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, mode, reserved_cpu_threads, reserved_memory_bytes, reserved_disk_bytes, updated_at FROM compute_policies;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) policies.Add(ReadPolicy(reader));
        return policies;
    }

    public async Task<ComputeJobView> CreateComputeJobAsync(string actor, ComputeJobRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ComputeJobView(Guid.NewGuid(), request.Name, "Queued", request.Priority,
            JsonSerializer.Serialize(request.Requirements, JsonOptions), JsonSerializer.Serialize(request.Command, JsonOptions),
            null, actor, now, null, null, null, null, 0, request.MaxAttempts, request.TimeoutSeconds);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM compute_jobs WHERE state IN ('Queued','Running');";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= ComputeLimits.MaxQueuedJobs)
                {
                    throw new InvalidOperationException("The compute queue is full.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO compute_jobs(id, name, state, priority, requirement_json, command_json, requested_by, created_at, max_attempts, timeout_seconds)
                VALUES ($id, $name, 'Queued', $priority, $requirements, $command, $actor, $now, $attempts, $timeout);
                """;
            command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name);
            command.Parameters.AddWithValue("$priority", request.Priority);
            command.Parameters.AddWithValue("$requirements", job.RequirementJson);
            command.Parameters.AddWithValue("$command", job.CommandJson);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            command.Parameters.AddWithValue("$attempts", Math.Clamp(request.MaxAttempts, 1, ComputeLimits.MaxAttempts));
            command.Parameters.AddWithValue("$timeout", Math.Clamp(request.TimeoutSeconds, ComputeLimits.MinTimeoutSeconds, ComputeLimits.MaxTimeoutSeconds));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "compute.job.create", job.Id.ToString("D"), "queued", $"name={request.Name} priority={request.Priority}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ComputeJobView>> GetComputeJobsAsync(string? state, int limit, CancellationToken cancellationToken = default)
    {
        var jobs = new List<ComputeJobView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, state, priority, requirement_json, command_json, assigned_device_id, requested_by, created_at, started_at, finished_at, result_json, error, attempts, max_attempts, timeout_seconds
            FROM compute_jobs WHERE ($state IS NULL OR state = $state)
            ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 300));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(ReadJob(reader));
        return jobs;
    }

    public async Task<IReadOnlyList<ComputeJobView>> GetQueuedComputeJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = new List<ComputeJobView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, state, priority, requirement_json, command_json, assigned_device_id, requested_by, created_at, started_at, finished_at, result_json, error, attempts, max_attempts, timeout_seconds
            FROM compute_jobs WHERE state = 'Queued' ORDER BY priority DESC, created_at LIMIT 50;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(ReadJob(reader));
        return jobs;
    }

    public async Task<ComputeJobView?> GetComputeJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, state, priority, requirement_json, command_json, assigned_device_id, requested_by, created_at, started_at, finished_at, result_json, error, attempts, max_attempts, timeout_seconds FROM compute_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<bool> AssignComputeJobAsync(Guid jobId, Guid deviceId, CancellationToken cancellationToken = default)
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
                UPDATE compute_jobs SET state = 'Running', assigned_device_id = $device, started_at = $now, attempts = attempts + 1
                WHERE id = $id AND state = 'Queued';
                """;
            command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return false;
            }
            await AppendAuditAsync(connection, transaction, "scheduler", "compute.job.assign", jobId.ToString("D"), "running", $"device={deviceId:D}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteComputeJobAsync(Guid jobId, string state, string? resultJson, string? error, CancellationToken cancellationToken = default)
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
                UPDATE compute_jobs SET state = $state, result_json = $result, error = $error, finished_at = $now
                WHERE id = $id AND state IN ('Queued','Running');
                """;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await AppendAuditAsync(connection, transaction, "scheduler", "compute.job.complete", jobId.ToString("D"), state.ToLowerInvariant(), error, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RequeueOrFailComputeJobAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
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
                UPDATE compute_jobs SET
                    state = CASE WHEN attempts < max_attempts THEN 'Queued' ELSE 'Failed' END,
                    error = $error,
                    assigned_device_id = NULL,
                    started_at = NULL,
                    finished_at = CASE WHEN attempts < max_attempts THEN NULL ELSE $now END
                WHERE id = $id AND state = 'Running';
                """;
            command.Parameters.AddWithValue("$error", error.Length > 2048 ? error[..2048] : error);
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CancelComputeJobAsync(Guid jobId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE compute_jobs SET state = 'Cancelled', finished_at = $now WHERE id = $id AND state IN ('Queued','Running');";
            command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return false;
            }
            await AppendAuditAsync(connection, transaction, actor, "compute.job.cancel", jobId.ToString("D"), "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ComputeJobView>> GetRunningComputeJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = new List<ComputeJobView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, state, priority, requirement_json, command_json, assigned_device_id, requested_by, created_at, started_at, finished_at, result_json, error, attempts, max_attempts, timeout_seconds
            FROM compute_jobs WHERE state = 'Running' AND assigned_device_id IS NOT NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(ReadJob(reader));
        return jobs;
    }

    private static ComputePolicyView ReadPolicy(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), ParseDate(reader.GetString(5)));

    private static ComputeJobView ReadJob(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
        reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
        reader.GetString(7), ParseDate(reader.GetString(8)),
        reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
        reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15));
}
