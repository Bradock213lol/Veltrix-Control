using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    public async Task<SoftwarePackageView> CreateSoftwarePackageAsync(string actor, SoftwarePackageRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var package = new SoftwarePackageView(Guid.NewGuid(), request.Name, request.Source.ToString(), request.PackageId, request.Version, request.Sha256, request.SilentArgs, actor, now);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM software_packages;";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= SoftwareLimits.MaxPackages)
                {
                    throw new InvalidOperationException("The package registry is full.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO software_packages(id, name, source, package_id, version, sha256, silent_args, created_by, created_at)
                VALUES ($id, $name, $source, $package, $version, $sha, $silent, $actor, $now);
                """;
            command.Parameters.AddWithValue("$id", package.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", request.Name);
            command.Parameters.AddWithValue("$source", request.Source.ToString());
            command.Parameters.AddWithValue("$package", request.PackageId);
            command.Parameters.AddWithValue("$version", (object?)request.Version ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", (object?)request.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$silent", (object?)request.SilentArgs ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "software.package.create", package.Id.ToString("D"), "succeeded", $"name={request.Name} source={request.Source}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return package;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SoftwarePackageView>> GetSoftwarePackagesAsync(CancellationToken cancellationToken = default)
    {
        var packages = new List<SoftwarePackageView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, source, package_id, version, sha256, silent_args, created_by, created_at FROM software_packages ORDER BY name COLLATE NOCASE LIMIT 500;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            packages.Add(new SoftwarePackageView(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), ParseDate(reader.GetString(8))));
        }
        return packages;
    }

    public async Task<SoftwareDeploymentView> CreateSoftwareDeploymentAsync(string actor, SoftwareDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.DeviceIds.Length is 0 or > SoftwareLimits.MaxDeploymentTargets)
        {
            throw new InvalidOperationException($"Choose between 1 and {SoftwareLimits.MaxDeploymentTargets} devices.");
        }

        var now = DateTimeOffset.UtcNow;
        var deploymentId = Guid.NewGuid();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var package = await LoadPackageAsync(connection, transaction, request.PackageId, cancellationToken)
                ?? throw new KeyNotFoundException("The package was not found.");

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO software_deployments(id, package_id, action, state, requested_by, created_at)
                    VALUES ($id, $package, $action, 'Running', $actor, $now);
                    """;
                insert.Parameters.AddWithValue("$id", deploymentId.ToString("D"));
                insert.Parameters.AddWithValue("$package", request.PackageId.ToString("D"));
                insert.Parameters.AddWithValue("$action", request.Action.ToString());
                insert.Parameters.AddWithValue("$actor", actor);
                insert.Parameters.AddWithValue("$now", Iso(now));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var targets = new List<SoftwareDeploymentTargetView>();
            foreach (var deviceId in request.DeviceIds.Distinct())
            {
                var operationId = Guid.NewGuid();
                var targetId = Guid.NewGuid();
                var argument = BuildOperationArgument(package, request.Action);
                await using var target = connection.CreateCommand();
                target.Transaction = transaction;
                target.CommandText = """
                    INSERT INTO operations(id, device_id, kind, state, argument, requested_by, requested_at)
                    SELECT $operation, id, $kind, 'Queued', $argument, $actor, $now FROM devices WHERE id = $device;
                    """;
                target.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                target.Parameters.AddWithValue("$device", deviceId.ToString("D"));
                target.Parameters.AddWithValue("$kind", request.Action switch
                {
                    SoftwareAction.Install => OperationKind.InstallSoftware.ToString(),
                    SoftwareAction.Uninstall => OperationKind.UninstallSoftware.ToString(),
                    _ => OperationKind.UpgradeSoftware.ToString()
                });
                target.Parameters.AddWithValue("$argument", argument);
                target.Parameters.AddWithValue("$actor", actor);
                target.Parameters.AddWithValue("$now", Iso(now));
                if (await target.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new KeyNotFoundException($"Device {deviceId:D} was not found.");
                }

                await using var targetInsert = connection.CreateCommand();
                targetInsert.Transaction = transaction;
                targetInsert.CommandText = """
                    INSERT INTO software_deployment_targets(id, deployment_id, device_id, operation_id, state)
                    VALUES ($id, $deployment, $device, $operation, 'Queued');
                    """;
                targetInsert.Parameters.AddWithValue("$id", targetId.ToString("D"));
                targetInsert.Parameters.AddWithValue("$deployment", deploymentId.ToString("D"));
                targetInsert.Parameters.AddWithValue("$device", deviceId.ToString("D"));
                targetInsert.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                await targetInsert.ExecuteNonQueryAsync(cancellationToken);
                targets.Add(new SoftwareDeploymentTargetView(targetId, deviceId, string.Empty, "Queued", null, null, null));
            }

            await AppendAuditAsync(connection, transaction, actor, "software.deploy", deploymentId.ToString("D"), "queued",
                $"package={package.Name} action={request.Action} devices={targets.Count}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SoftwareDeploymentView(deploymentId, request.PackageId, package.Name, request.Action.ToString(), "Running", actor, now, null, 0, 0, targets);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SoftwareDeploymentView?> GetSoftwareDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.package_id, p.name, d.action, d.state, d.requested_by, d.created_at, d.completed_at, d.success_count, d.failure_count
            FROM software_deployments d JOIN software_packages p ON p.id = d.package_id
            WHERE d.id = $id;
            """;
        command.Parameters.AddWithValue("$id", deploymentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var deployment = new SoftwareDeploymentView(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), ParseDate(reader.GetString(6)), reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
            reader.GetInt32(8), reader.GetInt32(9), []);
        await reader.DisposeAsync();

        var targets = new List<SoftwareDeploymentTargetView>();
        await using var targetCommand = connection.CreateCommand();
        targetCommand.CommandText = """
            SELECT t.id, t.device_id, COALESCE(dv.name, ''), t.state, t.error, t.started_at, t.finished_at
            FROM software_deployment_targets t LEFT JOIN devices dv ON dv.id = t.device_id
            WHERE t.deployment_id = $id ORDER BY dv.name COLLATE NOCASE;
            """;
        targetCommand.Parameters.AddWithValue("$id", deploymentId.ToString("D"));
        await using var targetReader = await targetCommand.ExecuteReaderAsync(cancellationToken);
        while (await targetReader.ReadAsync(cancellationToken))
        {
            targets.Add(new SoftwareDeploymentTargetView(
                Guid.Parse(targetReader.GetString(0)), Guid.Parse(targetReader.GetString(1)), targetReader.GetString(2),
                targetReader.GetString(3), targetReader.IsDBNull(4) ? null : targetReader.GetString(4),
                targetReader.IsDBNull(5) ? null : ParseDate(targetReader.GetString(5)),
                targetReader.IsDBNull(6) ? null : ParseDate(targetReader.GetString(6))));
        }
        return deployment with { Targets = targets };
    }

    public async Task<IReadOnlyList<SoftwareDeploymentView>> GetSoftwareDeploymentsAsync(int limit, CancellationToken cancellationToken = default)
    {
        var deployments = new List<SoftwareDeploymentView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.package_id, p.name, d.action, d.state, d.requested_by, d.created_at, d.completed_at, d.success_count, d.failure_count
            FROM software_deployments d JOIN software_packages p ON p.id = d.package_id
            ORDER BY d.created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            deployments.Add(new SoftwareDeploymentView(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), ParseDate(reader.GetString(6)), reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
                reader.GetInt32(8), reader.GetInt32(9), []));
        }
        return deployments;
    }

    public async Task<bool> CancelSoftwareDeploymentAsync(Guid deploymentId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var cancelOperations = connection.CreateCommand())
            {
                cancelOperations.Transaction = transaction;
                cancelOperations.CommandText = """
                    UPDATE operations SET state = 'Cancelled', finished_at = $now
                    WHERE state = 'Queued' AND id IN (
                        SELECT operation_id FROM software_deployment_targets
                        WHERE deployment_id = $id AND operation_id IS NOT NULL AND state = 'Queued');
                    """;
                cancelOperations.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
                cancelOperations.Parameters.AddWithValue("$id", deploymentId.ToString("D"));
                await cancelOperations.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var cancelTargets = connection.CreateCommand())
            {
                cancelTargets.Transaction = transaction;
                cancelTargets.CommandText = "UPDATE software_deployment_targets SET state = 'Cancelled', finished_at = $now WHERE deployment_id = $id AND state = 'Queued';";
                cancelTargets.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
                cancelTargets.Parameters.AddWithValue("$id", deploymentId.ToString("D"));
                await cancelTargets.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE software_deployments SET state = 'Cancelled', completed_at = $now WHERE id = $id AND state IN ('Queued','Running');";
                update.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("$id", deploymentId.ToString("D"));
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    return false;
                }
            }
            await AppendAuditAsync(connection, transaction, actor, "software.deploy.cancel", deploymentId.ToString("D"), "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordWindowsUpdateScanAsync(Guid deviceId, WindowsUpdateScanResult scan, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO windows_update_scans(id, device_id, scanned_at, pending_count, payload_json)
            VALUES ($id, $device, $scanned, $count, $payload);
            DELETE FROM windows_update_scans WHERE device_id = $device AND id NOT IN (
                SELECT id FROM windows_update_scans WHERE device_id = $device ORDER BY scanned_at DESC LIMIT 5);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$scanned", Iso(scan.ScannedAt));
        command.Parameters.AddWithValue("$count", scan.Updates.Count);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(scan, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WindowsUpdateScanResult?> GetLatestWindowsUpdateScanAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM windows_update_scans WHERE device_id = $device ORDER BY scanned_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<WindowsUpdateScanResult>(json, JsonOptions);
    }

    internal static async Task ApplyOperationToDeploymentAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, OperationState state, string? error, DateTimeOffset finishedAt, CancellationToken cancellationToken)
    {
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT deployment_id FROM software_deployment_targets WHERE operation_id = $operation;";
        lookup.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        var deploymentId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        if (deploymentId is null) return;

        var targetState = state switch
        {
            OperationState.Succeeded => "Completed",
            OperationState.Cancelled => "Cancelled",
            OperationState.TimedOut => "Failed",
            _ => state.ToString()
        };
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE software_deployment_targets SET state = $state, error = $error, started_at = COALESCE(started_at, $finished), finished_at = $finished
            WHERE operation_id = $operation;
            """;
        update.Parameters.AddWithValue("$state", targetState);
        update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        update.Parameters.AddWithValue("$finished", Iso(finishedAt));
        update.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        await using var counts = connection.CreateCommand();
        counts.Transaction = transaction;
        counts.CommandText = """
            SELECT
                SUM(CASE WHEN state = 'Completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN state IN ('Failed','Cancelled') THEN 1 ELSE 0 END),
                SUM(CASE WHEN state IN ('Queued','Running') THEN 1 ELSE 0 END)
            FROM software_deployment_targets WHERE deployment_id = $deployment;
            """;
        counts.Parameters.AddWithValue("$deployment", deploymentId);
        await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var success = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var failure = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        var pending = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        await reader.DisposeAsync();

        await using var deployment = connection.CreateCommand();
        deployment.Transaction = transaction;
        deployment.CommandText = """
            UPDATE software_deployments SET success_count = $success, failure_count = $failure,
                state = CASE WHEN $pending > 0 THEN 'Running'
                             WHEN $failure = 0 THEN 'Completed'
                             WHEN $success = 0 THEN 'Failed'
                             ELSE 'PartialSuccess' END,
                completed_at = CASE WHEN $pending > 0 THEN completed_at ELSE $finished END
            WHERE id = $deployment;
            """;
        deployment.Parameters.AddWithValue("$success", success);
        deployment.Parameters.AddWithValue("$failure", failure);
        deployment.Parameters.AddWithValue("$pending", pending);
        deployment.Parameters.AddWithValue("$finished", Iso(finishedAt));
        deployment.Parameters.AddWithValue("$deployment", deploymentId);
        await deployment.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PackageRecord(SoftwareSource Source, string PackageKey, string Name, string? Version, string? Sha256, string? SilentArgs);

    private static async Task<PackageRecord?> LoadPackageAsync(SqliteConnection connection, SqliteTransaction transaction, Guid packageId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source, package_id, name, version, sha256, silent_args FROM software_packages WHERE id = $id;";
        command.Parameters.AddWithValue("$id", packageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PackageRecord(
            Enum.Parse<SoftwareSource>(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static string BuildOperationArgument(PackageRecord package, SoftwareAction action)
    {
        var url = package.Source is SoftwareSource.Msi or SoftwareSource.Exe ? package.PackageKey : null;
        return action switch
        {
            SoftwareAction.Install => JsonSerializer.Serialize(new SoftwareInstallArgument(package.Source, package.PackageKey, package.Version, url, package.Sha256, package.SilentArgs), JsonOptions),
            _ => JsonSerializer.Serialize(new SoftwareUninstallArgument(package.Source, package.PackageKey, package.Name), JsonOptions)
        };
    }
}
