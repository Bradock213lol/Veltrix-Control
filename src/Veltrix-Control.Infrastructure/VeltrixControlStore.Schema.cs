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
