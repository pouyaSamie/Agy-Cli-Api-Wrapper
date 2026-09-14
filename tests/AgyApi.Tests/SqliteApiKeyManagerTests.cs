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
        
        // Open a master connection to keep the shared in-memory database alive for the test
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
    public async Task CreateKey_PersistsToSqlite_AndCanAuthenticate()
    {
        var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);

        var key = await manager.CreateKeyAsync("Test Client");

        Assert.NotNull(key);
        Assert.StartsWith("agy-live-", key.Key);
        Assert.Equal("Test Client", key.Name);
        Assert.True(manager.HasAnyKeys());

        var authenticated = manager.AuthenticateKey(key.Key);
        Assert.NotNull(authenticated);
        Assert.Equal(key.Id, authenticated.Id);
    }

    [Fact]
    public async Task RecordUsage_UpdatesTokens_Persistently()
    {
        var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var key = await manager.CreateKeyAsync("Usage Test Client");

        await manager.RecordUsageAsync(key.Id, 100, 50, 150);
        await manager.RecordUsageAsync(key.Id, 200, 100, 300);

        var keys = await manager.GetKeysAsync();
        var myKey = keys.FirstOrDefault(k => k.Id == key.Id);

        Assert.NotNull(myKey);
        Assert.Equal(300, myKey.PromptTokens);
        Assert.Equal(150, myKey.CompletionTokens);
        Assert.Equal(450, myKey.TotalTokens);
        Assert.Equal(2, myKey.RequestCount);
    }

    [Fact]
    public async Task Database_PersistsKeys_AcrossManagerInstances()
    {
        var manager1 = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var created = await manager1.CreateKeyAsync("Persistent Client");
        await manager1.RecordUsageAsync(created.Id, 500, 200, 700);

        // Simulate server restart: new instance reading from the same shared SQLite database
        var manager2 = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);

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
        var manager = new ApiKeyManager(_connectionString, _options, NullLogger<ApiKeyManager>.Instance);
        var key = await manager.CreateKeyAsync("Temporary Client");

        var revoked = await manager.RevokeKeyAsync(key.Id);
        Assert.True(revoked);

        var authenticated = manager.AuthenticateKey(key.Key);
        Assert.Null(authenticated);
    }
}
