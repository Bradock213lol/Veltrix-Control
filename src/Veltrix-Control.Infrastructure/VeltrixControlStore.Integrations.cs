using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using VeltrixControl.Contracts;

namespace VeltrixControl.Infrastructure;

public sealed class IntegrationCredentialProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public IntegrationCredentialProtector(IOptions<StoreOptions> options)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath)) ?? ".";
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "integration-credentials.key");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            if (_key.Length != 32) throw new InvalidDataException("The integration credential key is invalid.");
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        var temporary = keyPath + ".new";
        File.WriteAllBytes(temporary, _key);
        File.Move(temporary, keyPath, overwrite: true);
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return Convert.ToBase64String(payload);
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var payload = Convert.FromBase64String(protectedValue);
            if (payload.Length < NonceSize + TagSize) return null;
            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var cipher = payload.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

public sealed partial class VeltrixControlStore
{
    public async Task<IntegrationView> CreateIntegrationAsync(string actor, IntegrationRequest request, string? protectedCredential, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM integrations;";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) >= IntegrationLimits.MaxIntegrations)
                {
                    throw new InvalidOperationException("The integration limit has been reached.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO integrations(id, kind, name, base_url, credential, enabled, created_by, created_at, updated_at)
                VALUES ($id, $kind, $name, $url, $credential, $enabled, $actor, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$kind", request.Kind);
            command.Parameters.AddWithValue("$name", request.Name);
            command.Parameters.AddWithValue("$url", request.BaseUrl);
            command.Parameters.AddWithValue("$credential", (object?)protectedCredential ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$now", Iso(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AppendAuditAsync(connection, transaction, actor, "integration.create", id.ToString("D"), "succeeded", $"kind={request.Kind}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationView(id, request.Kind, request.Name, request.BaseUrl, request.Enabled, protectedCredential is not null,
                null, null, null, actor, now, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<IntegrationView>> GetIntegrationsAsync(CancellationToken cancellationToken = default)
    {
        var integrations = new List<IntegrationView>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, name, base_url, credential IS NOT NULL, enabled, health_state, health_detail, last_checked_at, created_by, created_at, updated_at FROM integrations ORDER BY name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) integrations.Add(ReadIntegration(reader));
        return integrations;
    }

    public async Task<IntegrationView?> GetIntegrationAsync(Guid integrationId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, name, base_url, credential IS NOT NULL, enabled, health_state, health_detail, last_checked_at, created_by, created_at, updated_at FROM integrations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", integrationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntegration(reader) : null;
    }

    public async Task<(string Kind, string BaseUrl, string? ProtectedCredential)?> GetIntegrationConnectionAsync(Guid integrationId, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, base_url, credential FROM integrations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", integrationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task UpdateIntegrationHealthAsync(Guid integrationId, string state, string? detail, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE integrations SET health_state = $state, health_detail = $detail, last_checked_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", integrationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteIntegrationAsync(Guid integrationId, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM integrations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", integrationId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            await AppendAuditAsync(connection, transaction, actor, "integration.delete", integrationId.ToString("D"), "succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IntegrationView ReadIntegration(System.Data.Common.DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(5) == 1,
        reader.GetInt32(4) == 1, reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)), reader.GetString(9), ParseDate(reader.GetString(10)), ParseDate(reader.GetString(11)));
}
