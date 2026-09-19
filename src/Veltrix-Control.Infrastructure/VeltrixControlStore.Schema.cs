using Microsoft.Data.Sqlite;

namespace VeltrixControl.Infrastructure;

public sealed partial class VeltrixControlStore
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS schema_versions (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY,
            username TEXT NOT NULL COLLATE NOCASE UNIQUE,
            password_hash TEXT NOT NULL,
            role TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS enrollment_tokens (
            id TEXT PRIMARY KEY,
            token_hash TEXT NOT NULL UNIQUE,
            expires_at TEXT NOT NULL,
            used_at TEXT NULL,
            revoked_at TEXT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            public_key TEXT NOT NULL,
            inventory_json TEXT NOT NULL,
            telemetry_json TEXT NULL,
            enrolled_at TEXT NOT NULL,
            last_heartbeat TEXT NOT NULL,
            agent_version TEXT NOT NULL,
            is_simulation INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS used_nonces (
            device_id TEXT NOT NULL,
            nonce TEXT NOT NULL,
            used_at TEXT NOT NULL,
            PRIMARY KEY (device_id, nonce),
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS metrics (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            cpu_percent REAL NOT NULL,
            used_memory_bytes INTEGER NOT NULL,
            total_memory_bytes INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_metrics_device_time ON metrics(device_id, captured_at DESC);

        CREATE TABLE IF NOT EXISTS operations (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            state TEXT NOT NULL,
            argument TEXT NULL,
            result_json TEXT NULL,
            error TEXT NULL,
            requested_by TEXT NOT NULL,
            requested_at TEXT NOT NULL,
            started_at TEXT NULL,
            finished_at TEXT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_operations_device_state ON operations(device_id, state, requested_at);

        CREATE TABLE IF NOT EXISTS audit_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            target TEXT NOT NULL,
            outcome TEXT NOT NULL,
            metadata TEXT NULL,
            previous_hash TEXT NOT NULL,
            event_hash TEXT NOT NULL UNIQUE
        );

        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES (1, CURRENT_TIMESTAMP);

        CREATE TABLE IF NOT EXISTS transfers (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            direction TEXT NOT NULL,
            state TEXT NOT NULL,
            path TEXT NOT NULL,
            total_bytes INTEGER NOT NULL,
            bytes_transferred INTEGER NOT NULL DEFAULT 0,
            sha256 TEXT NULL,
            error TEXT NULL,
            requested_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_transfers_device ON transfers(device_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS terminal_sessions (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            shell TEXT NOT NULL,
            working_directory TEXT NOT NULL,
            state TEXT NOT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_activity TEXT NOT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_terminal_sessions_device ON terminal_sessions(device_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS terminal_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            device_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            kind TEXT NOT NULL,
            data TEXT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_terminal_events_session ON terminal_events(session_id, id);

        CREATE TABLE IF NOT EXISTS software_packages (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            source TEXT NOT NULL,
            package_id TEXT NOT NULL,
            version TEXT NULL,
            sha256 TEXT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS software_deployments (
            id TEXT PRIMARY KEY,
            package_id TEXT NOT NULL,
            action TEXT NOT NULL,
            state TEXT NOT NULL,
            requested_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT NULL,
            success_count INTEGER NOT NULL DEFAULT 0,
            failure_count INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (package_id) REFERENCES software_packages(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS software_deployment_targets (
            id TEXT PRIMARY KEY,
            deployment_id TEXT NOT NULL,
            device_id TEXT NOT NULL,
            state TEXT NOT NULL,
            error TEXT NULL,
            started_at TEXT NULL,
            finished_at TEXT NULL,
            FOREIGN KEY (deployment_id) REFERENCES software_deployments(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_software_targets_deployment ON software_deployment_targets(deployment_id, state);

        CREATE TABLE IF NOT EXISTS windows_update_scans (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            scanned_at TEXT NOT NULL,
            pending_count INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_update_scans_device ON windows_update_scans(device_id, scanned_at DESC);

        CREATE TABLE IF NOT EXISTS backups (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            name TEXT NOT NULL,
            source_path TEXT NOT NULL,
            archive_path TEXT NOT NULL,
            state TEXT NOT NULL,
            size_bytes INTEGER NOT NULL DEFAULT 0,
            sha256 TEXT NULL,
            error TEXT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT NULL,
            retention_days INTEGER NOT NULL DEFAULT 30,
            schedule_cron TEXT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_backups_device ON backups(device_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS alerts (
            id TEXT PRIMARY KEY,
            device_id TEXT NULL,
            severity TEXT NOT NULL,
            code TEXT NOT NULL,
            title TEXT NOT NULL,
            message TEXT NOT NULL,
            state TEXT NOT NULL,
            created_at TEXT NOT NULL,
            acknowledged_by TEXT NULL,
            acknowledged_at TEXT NULL,
            metadata TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_alerts_state ON alerts(state, created_at DESC);

        CREATE TABLE IF NOT EXISTS automations (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            trigger_json TEXT NOT NULL,
            condition_json TEXT NULL,
            action_json TEXT NOT NULL,
            cooldown_seconds INTEGER NOT NULL DEFAULT 300,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_run_at TEXT NULL,
            run_count INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS automation_runs (
            id TEXT PRIMARY KEY,
            automation_id TEXT NOT NULL,
            device_id TEXT NULL,
            state TEXT NOT NULL,
            detail TEXT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT NULL,
            FOREIGN KEY (automation_id) REFERENCES automations(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_automation_runs ON automation_runs(automation_id, started_at DESC);

        CREATE TABLE IF NOT EXISTS compute_policies (
            device_id TEXT PRIMARY KEY,
            mode TEXT NOT NULL,
            reserved_cpu_threads INTEGER NOT NULL DEFAULT 0,
            reserved_memory_bytes INTEGER NOT NULL DEFAULT 0,
            reserved_disk_bytes INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS compute_jobs (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            state TEXT NOT NULL,
            priority INTEGER NOT NULL DEFAULT 100,
            requirement_json TEXT NOT NULL,
            command_json TEXT NOT NULL,
            assigned_device_id TEXT NULL,
            requested_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            started_at TEXT NULL,
            finished_at TEXT NULL,
            result_json TEXT NULL,
            error TEXT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            max_attempts INTEGER NOT NULL DEFAULT 2,
            timeout_seconds INTEGER NOT NULL DEFAULT 3600
        );
        CREATE INDEX IF NOT EXISTS ix_compute_jobs_state ON compute_jobs(state, priority, created_at);

        CREATE TABLE IF NOT EXISTS game_servers (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            name TEXT NOT NULL,
            adapter TEXT NOT NULL,
            state TEXT NOT NULL,
            install_path TEXT NOT NULL,
            port INTEGER NOT NULL,
            memory_mb INTEGER NOT NULL DEFAULT 2048,
            cpu_threads INTEGER NOT NULL DEFAULT 2,
            auto_restart INTEGER NOT NULL DEFAULT 1,
            environment_json TEXT NULL,
            version TEXT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_started_at TEXT NULL,
            last_stopped_at TEXT NULL,
            FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_game_servers_device ON game_servers(device_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS game_server_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            server_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            data TEXT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_game_server_events ON game_server_events(server_id, id);

        CREATE TABLE IF NOT EXISTS integrations (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            base_url TEXT NOT NULL,
            credential TEXT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            health_state TEXT NULL,
            last_checked_at TEXT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES (2, CURRENT_TIMESTAMP);
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES (3, CURRENT_TIMESTAMP);
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES (4, CURRENT_TIMESTAMP);
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES (5, CURRENT_TIMESTAMP);
        INSERT OR IGNORE INTO schema_versions(version, applied_at) VALUES (6, CURRENT_TIMESTAMP);
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true
    }.ToString());
}
