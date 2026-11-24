using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgyApi.Tests;

public class SqliteApiKeyManagerTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly IOptions<AgyOptions> _options;

    public SqliteApiKeyManagerTests()
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N");
        _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        _options = Options.Create(new AgyOptions
        {
            ApiKey = "static-test-key",
            DatabasePath = ":memory:"
        });
    }

    public void Dispose()
    {
        _keepAliveConnection.Dispose();
    }

    [Fact]
    public async Task CreateKey_PersistsHashedKeyOnly_AndCanAuthenticate()
    {
        await using var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);

        var key = await manager.CreateKeyAsync("Test Client");

        Assert.NotNull(key);
        Assert.StartsWith("agy-live-", key.Key);
        Assert.Equal("Test Client", key.Name);
        Assert.True(manager.HasAnyKeys());

        // Verify plaintext secret is NOT stored in SQLite; only SHA-256 key_hash exists
        using var cmd = _keepAliveConnection.CreateCommand();
        cmd.CommandText = "SELECT key_hash FROM api_keys WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", key.Id);
        var storedHash = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(storedHash);
        Assert.NotEqual(key.Key, storedHash);
        Assert.Equal(ApiKeyManager.HashKey(key.Key), storedHash);

        var authenticated = manager.AuthenticateKey(key.Key);
        Assert.NotNull(authenticated);
        Assert.Equal(key.Id, authenticated.Id);
        Assert.Equal(string.Empty, authenticated.Key); // No plaintext retained in memory
    }

    [Fact]
    public async Task Startup_AutoMigratesLegacyPlaintextSchemaToHashedKey()
    {
        // Seed legacy schema with plaintext `key` column before initializing ApiKeyManager
        using (var setupCmd = _keepAliveConnection.CreateCommand())
        {
            setupCmd.CommandText = @"
                CREATE TABLE api_keys (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    key TEXT NOT NULL UNIQUE,
                    masked_key TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    last_used_at TEXT,
                    prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    completion_tokens INTEGER NOT NULL DEFAULT 0,
                    total_tokens INTEGER NOT NULL DEFAULT 0,
                    request_count INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO api_keys (id, name, key, masked_key, created_at, prompt_tokens, completion_tokens, total_tokens, request_count)
                VALUES ('legacy-1', 'Legacy Key', 'agy-live-legacysecret123456', 'agy-live-l...3456', '2026-01-01T00:00:00Z', 10, 20, 30, 1);
            ";
            setupCmd.ExecuteNonQuery();
        }

        await using var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);

        var auth = manager.AuthenticateKey("agy-live-legacysecret123456");
        Assert.NotNull(auth);
        Assert.Equal("legacy-1", auth.Id);
        Assert.Equal(30, auth.TotalTokens);

        // Verify legacy plaintext column is gone and replaced by key_hash
        using var checkCmd = _keepAliveConnection.CreateCommand();
        checkCmd.CommandText = "SELECT key_hash FROM api_keys WHERE id = 'legacy-1';";
        var hash = (string?)await checkCmd.ExecuteScalarAsync();
        Assert.Equal(ApiKeyManager.HashKey("agy-live-legacysecret123456"), hash);
    }

    [Fact]
    public async Task RecordUsage_AndResetUsage_PersistCorrectly()
    {
        await using var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var key = await manager.CreateKeyAsync("Usage Test Client");

        await manager.RecordUsageAsync(key.Id, 100, 50, 150);
        await manager.RecordUsageAsync(key.Id, 200, 100, 300);

        var keys = await manager.GetKeysAsync();
        var myKey = keys.First(k => k.Id == key.Id);
        Assert.Equal(300, myKey.PromptTokens);
        Assert.Equal(150, myKey.CompletionTokens);
        Assert.Equal(450, myKey.TotalTokens);
        Assert.Equal(2, myKey.RequestCount);

        var reset = await manager.ResetUsageAsync(key.Id);
        Assert.True(reset);

        var keysAfterReset = await manager.GetKeysAsync();
        var resetKey = keysAfterReset.First(k => k.Id == key.Id);
        Assert.Equal(0, resetKey.TotalTokens);
        Assert.Equal(0, resetKey.RequestCount);
    }

    [Fact]
    public async Task ConcurrentAuthAndUsage_NoSqliteBusyAndExactTotals()
    {
        await using var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var key = await manager.CreateKeyAsync("Stress Client");

        var tasks = Enumerable.Range(0, 50).Select(async _ =>
        {
            var authed = manager.AuthenticateKey(key.Key);
            Assert.NotNull(authed);
            await manager.RecordUsageAsync(key.Id, 10, 5, 15);
        });

        await Task.WhenAll(tasks);
        await manager.FlushPendingUpdatesAsync();

        var keys = await manager.GetKeysAsync();
        var result = keys.First(k => k.Id == key.Id);
        Assert.Equal(500, result.PromptTokens);
        Assert.Equal(250, result.CompletionTokens);
        Assert.Equal(750, result.TotalTokens);
        Assert.Equal(50, result.RequestCount);
        Assert.True(await manager.IsHealthyAsync());
    }

    [Fact]
    public async Task Database_PersistsKeys_AcrossManagerInstances()
    {
        await using var manager1 = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var created = await manager1.CreateKeyAsync("Persistent Client");
        await manager1.RecordUsageAsync(created.Id, 500, 200, 700);

        await using var manager2 = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);

        Assert.True(manager2.HasAnyKeys());
        var authenticated = manager2.AuthenticateKey(created.Key);
        Assert.NotNull(authenticated);
        Assert.Equal("Persistent Client", authenticated.Name);

        var keys = await manager2.GetKeysAsync();
        var reloadedKey = keys.FirstOrDefault(k => k.Id == created.Id);
        Assert.NotNull(reloadedKey);
        Assert.Equal(700, reloadedKey.TotalTokens);
        Assert.Equal(1, reloadedKey.RequestCount);
    }

    [Fact]
    public async Task RevokeKey_DeletesFromDatabase()
    {
        await using var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var key = await manager.CreateKeyAsync("Temporary Client");

        var revoked = await manager.RevokeKeyAsync(key.Id);
        Assert.True(revoked);

        var authenticated = manager.AuthenticateKey(key.Key);
        Assert.Null(authenticated);
    }
}
