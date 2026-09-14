using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AgyApi.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgyApi.Features.ApiKeys;

public class ApiKeyManager : IApiKeyManager, IApiKeyAuthenticator, IApiKeyUsageRecorder
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly AgyOptions _options;
    private readonly ILogger<ApiKeyManager> _logger;
    private readonly ConcurrentDictionary<string, ApiKeyItem> _keysById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ApiKeyItem> _keysBySecret = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _initLock = new();
    private bool _initialized;

    public ApiKeyManager(IWebHostEnvironment env, IOptions<AgyOptions> options, ILogger<ApiKeyManager> logger)
    {
        _options = options.Value;
        _logger = logger;

        var configuredPath = !string.IsNullOrWhiteSpace(_options.DatabasePath)
            ? _options.DatabasePath
            : "data/agy_gateway.db";

        if (Path.IsPathRooted(configuredPath))
        {
            _dbPath = configuredPath;
        }
        else
        {
            if (Directory.Exists("/app/data") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            {
                _dbPath = Path.Combine("/app", configuredPath);
            }
            else
            {
                var repoRootData = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", configuredPath));
                var localData = Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));

                if (Directory.Exists(Path.GetDirectoryName(repoRootData)))
                {
                    _dbPath = repoRootData;
                }
                else
                {
                    _dbPath = localData;
                }
            }
        }

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        // Initialize SQLite schema and load keys immediately on startup
        EnsureDatabaseInitialized(env);
    }

    // Constructor for unit tests with in-memory or custom SQLite connection string
    public ApiKeyManager(string connectionString, IOptions<AgyOptions> options, ILogger<ApiKeyManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connectionString = connectionString;
        _dbPath = ":memory:";
        EnsureDatabaseInitialized(null);
    }

    private void EnsureDatabaseInitialized(IWebHostEnvironment? env)
    {
        lock (_initLock)
        {
            if (_initialized) return;

            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using (var pragmaCmd = conn.CreateCommand())
                {
                    pragmaCmd.CommandText = @"
                        PRAGMA journal_mode = WAL;
                        PRAGMA busy_timeout = 5000;
                        PRAGMA synchronous = NORMAL;
                    ";
                    pragmaCmd.ExecuteNonQuery();
                }

                using (var tableCmd = conn.CreateCommand())
                {
                    tableCmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS api_keys (
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
                        CREATE INDEX IF NOT EXISTS idx_api_keys_key ON api_keys(key);
                    ";
                    tableCmd.ExecuteNonQuery();
                }

                // Check count to see if we should auto-migrate from keys.json
                long count = 0;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM api_keys;";
                    count = (long)(countCmd.ExecuteScalar() ?? 0L);
                }

                if (count == 0 && env != null)
                {
                    AutoMigrateFromJsonFiles(conn, env);
                }

                // Load all existing keys into in-memory cache for O(1) authorization
                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.CommandText = @"
                        SELECT id, name, key, masked_key, created_at, last_used_at,
                               prompt_tokens, completion_tokens, total_tokens, request_count
                        FROM api_keys;
                    ";

                    using var reader = selectCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var item = new ApiKeyItem
                        {
                            Id = reader.GetString(0),
                            Name = reader.GetString(1),
                            Key = reader.GetString(2),
                            MaskedKey = reader.GetString(3),
                            CreatedAtUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                            LastUsedAtUtc = !reader.IsDBNull(5) ? DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
                            PromptTokens = reader.GetInt64(6),
                            CompletionTokens = reader.GetInt64(7),
                            TotalTokens = reader.GetInt64(8),
                            RequestCount = reader.GetInt64(9)
                        };

                        _keysById[item.Id] = item;
                        _keysBySecret[item.Key] = item;
                    }
                }

                _logger.LogInformation("SQLite database initialized at {Path}. Loaded {Count} keys.", _dbPath, _keysById.Count);
                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database at {Path}", _dbPath);
                throw;
            }
        }
    }

    private void AutoMigrateFromJsonFiles(SqliteConnection conn, IWebHostEnvironment env)
    {
        var dbDir = Path.GetDirectoryName(_dbPath) ?? "";
        var candidates = new List<string>
        {
            Path.Combine(dbDir, "keys.json"),
            Path.Combine(env.ContentRootPath, "..", "..", "data", "keys.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "keys.json"),
            Path.Combine(env.ContentRootPath, "data", "keys.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "keys.json"),
            Path.Combine(env.ContentRootPath, "keys.json")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var items = JsonSerializer.Deserialize<List<ApiKeyItem>>(json);
                    if (items != null && items.Count > 0)
                    {
                        using var tx = conn.BeginTransaction();
                        foreach (var item in items)
                        {
                            if (string.IsNullOrWhiteSpace(item.Key)) continue;

                            using var insertCmd = conn.CreateCommand();
                            insertCmd.Transaction = tx;
                            insertCmd.CommandText = @"
                                INSERT OR IGNORE INTO api_keys
                                (id, name, key, masked_key, created_at, last_used_at, prompt_tokens, completion_tokens, total_tokens, request_count)
                                VALUES (@id, @name, @key, @masked, @created, @lastUsed, @prompt, @comp, @total, @reqs);
                            ";
                            insertCmd.Parameters.AddWithValue("@id", item.Id ?? Guid.NewGuid().ToString("N"));
                            insertCmd.Parameters.AddWithValue("@name", item.Name ?? "Migrated Key");
                            insertCmd.Parameters.AddWithValue("@key", item.Key);
                            insertCmd.Parameters.AddWithValue("@masked", item.MaskedKey ?? (item.Key.Length > 12 ? $"{item.Key[..10]}...{item.Key[^4..]}" : item.Key));
                            insertCmd.Parameters.AddWithValue("@created", item.CreatedAtUtc.ToString("o"));
                            insertCmd.Parameters.AddWithValue("@lastUsed", item.LastUsedAtUtc.HasValue ? item.LastUsedAtUtc.Value.ToString("o") : (object)DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@prompt", item.PromptTokens);
                            insertCmd.Parameters.AddWithValue("@comp", item.CompletionTokens);
                            insertCmd.Parameters.AddWithValue("@total", item.TotalTokens);
                            insertCmd.Parameters.AddWithValue("@reqs", item.RequestCount);
                            insertCmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                        _logger.LogInformation("Successfully migrated {Count} keys from {JsonPath} to SQLite.", items.Count, path);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not auto-migrate keys from {Path}", path);
                }
            }
        }
    }

    public Task InitializeAsync()
    {
        // Already initialized in constructor
        return Task.CompletedTask;
    }

    public bool HasAnyKeys()
    {
        return !_keysById.IsEmpty || !string.IsNullOrWhiteSpace(_options.ApiKey);
    }

    public bool IsValidKey(string? providedKey)
    {
        return AuthenticateKey(providedKey) != null;
    }

    public ApiKeyItem? AuthenticateKey(string? providedKey)
    {
        if (string.IsNullOrWhiteSpace(providedKey)) return null;

        // Check appsettings.json static key
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            string.Equals(providedKey, _options.ApiKey, StringComparison.Ordinal))
        {
            return new ApiKeyItem
            {
                Id = "config-static-key",
                Name = "appsettings.json Key",
                Key = _options.ApiKey,
                LastUsedAtUtc = DateTime.UtcNow
            };
        }

        // Check in-memory index loaded from SQLite
        if (_keysBySecret.TryGetValue(providedKey, out var matched))
        {
            matched.LastUsedAtUtc = DateTime.UtcNow;
            _ = UpdateLastUsedTimestampAsync(matched.Id, matched.LastUsedAtUtc.Value);
            return matched;
        }

        return null;
    }

    private async Task UpdateLastUsedTimestampAsync(string id, DateTime lastUsed)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE api_keys SET last_used_at = @lastUsed WHERE id = @id;";
            cmd.Parameters.AddWithValue("@lastUsed", lastUsed.ToString("o"));
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update last_used_at in SQLite for key {Id}", id);
        }
    }

    public async Task<ApiKeyItem> CreateKeyAsync(string name)
    {
        var keyBytes = new byte[24];
        RandomNumberGenerator.Fill(keyBytes);
        var generatedKey = "agy-live-" + Convert.ToHexString(keyBytes).ToLowerInvariant();

        var item = new ApiKeyItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "API Key" : name.Trim(),
            Key = generatedKey,
            MaskedKey = $"{generatedKey[..10]}...{generatedKey[^4..]}",
            CreatedAtUtc = DateTime.UtcNow,
            LastUsedAtUtc = null,
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            RequestCount = 0
        };

        await _writeLock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO api_keys 
                (id, name, key, masked_key, created_at, last_used_at, prompt_tokens, completion_tokens, total_tokens, request_count)
                VALUES (@id, @name, @key, @masked, @created, @lastUsed, @prompt, @comp, @total, @reqs);
            ";
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.Parameters.AddWithValue("@name", item.Name);
            cmd.Parameters.AddWithValue("@key", item.Key);
            cmd.Parameters.AddWithValue("@masked", item.MaskedKey);
            cmd.Parameters.AddWithValue("@created", item.CreatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@lastUsed", DBNull.Value);
            cmd.Parameters.AddWithValue("@prompt", item.PromptTokens);
            cmd.Parameters.AddWithValue("@comp", item.CompletionTokens);
            cmd.Parameters.AddWithValue("@total", item.TotalTokens);
            cmd.Parameters.AddWithValue("@reqs", item.RequestCount);
            await cmd.ExecuteNonQueryAsync();

            _keysById[item.Id] = item;
            _keysBySecret[item.Key] = item;

            _logger.LogInformation("Created new SQLite API key: {Name} ({Masked})", item.Name, item.MaskedKey);
            return item;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<ApiKeyItem>> GetKeysAsync()
    {
        var list = _keysById.Values
            .OrderByDescending(k => k.CreatedAtUtc)
            .Select(k => new ApiKeyItem
            {
                Id = k.Id,
                Name = k.Name,
                Key = string.Empty, // Mask secret from list output
                MaskedKey = k.MaskedKey,
                CreatedAtUtc = k.CreatedAtUtc,
                LastUsedAtUtc = k.LastUsedAtUtc,
                PromptTokens = k.PromptTokens,
                CompletionTokens = k.CompletionTokens,
                TotalTokens = k.TotalTokens,
                RequestCount = k.RequestCount
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ApiKeyItem>>(list);
    }

    public async Task RecordUsageAsync(string keyId, long promptTokens, long completionTokens, long totalTokens)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return;

        if (_keysById.TryGetValue(keyId, out var matched))
        {
            await _writeLock.WaitAsync();
            try
            {
                matched.PromptTokens += promptTokens;
                matched.CompletionTokens += completionTokens;
                matched.TotalTokens += totalTokens;
                matched.RequestCount++;
                matched.LastUsedAtUtc = DateTime.UtcNow;

                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE api_keys
                    SET prompt_tokens = prompt_tokens + @prompt,
                        completion_tokens = completion_tokens + @comp,
                        total_tokens = total_tokens + @total,
                        request_count = request_count + 1,
                        last_used_at = @lastUsed
                    WHERE id = @id;
                ";
                cmd.Parameters.AddWithValue("@prompt", promptTokens);
                cmd.Parameters.AddWithValue("@comp", completionTokens);
                cmd.Parameters.AddWithValue("@total", totalTokens);
                cmd.Parameters.AddWithValue("@lastUsed", matched.LastUsedAtUtc.Value.ToString("o"));
                cmd.Parameters.AddWithValue("@id", keyId);
                await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation("SQLite: updated token usage for key {Id} ({Name}): +{Prompt} prompt, +{Completion} completion, +{Total} total. Cumulative: {CumTotal} tokens, {Reqs} reqs",
                    matched.Id, matched.Name, promptTokens, completionTokens, totalTokens, matched.TotalTokens, matched.RequestCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist token usage to SQLite for key {KeyId}", keyId);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    public async Task<bool> ResetUsageAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        if (_keysById.TryGetValue(id, out var matched))
        {
            matched.PromptTokens = 0;
            matched.CompletionTokens = 0;
            matched.TotalTokens = 0;
            matched.RequestCount = 0;

            await _writeLock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE api_keys
                    SET prompt_tokens = 0, completion_tokens = 0, total_tokens = 0, request_count = 0
                    WHERE id = @id;
                ";
                cmd.Parameters.AddWithValue("@id", id);
                var rows = await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation("SQLite: reset token usage counters for key {Id} ({Name}) to 0", matched.Id, matched.Name);
                return rows > 0;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        return false;
    }

    public async Task<bool> RevokeKeyAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM api_keys WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            var rows = await cmd.ExecuteNonQueryAsync();

            if (_keysById.TryRemove(id, out var removed))
            {
                _keysBySecret.TryRemove(removed.Key, out _);
            }

            _logger.LogInformation("SQLite: revoked API key with ID {Id}", id);
            return rows > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
