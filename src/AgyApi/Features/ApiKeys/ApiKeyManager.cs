using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgyApi.Configuration;
using AgyApi.Infrastructure.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgyApi.Features.ApiKeys;

public class ApiKeyManager : IApiKeyManager, IApiKeyAuthenticator, IApiKeyUsageRecorder, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly AgyOptions _options;
    private readonly ILogger<ApiKeyManager> _logger;
    private readonly ConcurrentDictionary<string, ApiKeyItem> _keysById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ApiKeyItem> _keysByHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idToHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingLastUsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _initLock = new();
    private readonly CancellationTokenSource _flushCts = new();
    private readonly Task _flushTask;
    private bool _initialized;
    private int _disposed;

    [ActivatorUtilitiesConstructor]
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
        else if (Directory.Exists("/app/data") ||
                 string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            _dbPath = Path.Combine("/app", configuredPath);
        }
        else
        {
            _dbPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));
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
            Cache = SqliteCacheMode.Default
        }.ToString();

        EnsureDatabaseInitialized(env);
        _flushTask = RunLastUsedFlushLoopAsync(_flushCts.Token);
    }

    // Constructor for unit tests with in-memory or custom SQLite connection string
    public ApiKeyManager(string connectionString, IOptions<AgyOptions> options, ILogger<ApiKeyManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connectionString = connectionString;
        _dbPath = ":memory:";
        EnsureDatabaseInitialized(null);
        _flushTask = RunLastUsedFlushLoopAsync(_flushCts.Token);
    }

    internal static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
        return conn;
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

                EnsureHashedSchema(conn);

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

                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.CommandText = @"
                        SELECT id, name, key_hash, masked_key, created_at, last_used_at,
                               prompt_tokens, completion_tokens, total_tokens, request_count
                        FROM api_keys;
                    ";

                    using var reader = selectCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var keyHash = reader.GetString(2);
                        var item = new ApiKeyItem
                        {
                            Id = reader.GetString(0),
                            Name = reader.GetString(1),
                            Key = string.Empty,
                            MaskedKey = reader.GetString(3),
                            CreatedAtUtc = DateTime.Parse(reader.GetString(4), null, DateTimeStyles.RoundtripKind),
                            LastUsedAtUtc = !reader.IsDBNull(5) ? DateTime.Parse(reader.GetString(5), null, DateTimeStyles.RoundtripKind) : null,
                            PromptTokens = reader.GetInt64(6),
                            CompletionTokens = reader.GetInt64(7),
                            TotalTokens = reader.GetInt64(8),
                            RequestCount = reader.GetInt64(9)
                        };

                        _keysById[item.Id] = item;
                        _keysByHash[keyHash] = item;
                        _idToHash[item.Id] = keyHash;
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

    private void EnsureHashedSchema(SqliteConnection conn)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCmd = conn.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(api_keys);";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Count == 0)
        {
            using var createCmd = conn.CreateCommand();
            createCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS api_keys (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    key_hash TEXT NOT NULL UNIQUE,
                    masked_key TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    last_used_at TEXT,
                    prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    completion_tokens INTEGER NOT NULL DEFAULT 0,
                    total_tokens INTEGER NOT NULL DEFAULT 0,
                    request_count INTEGER NOT NULL DEFAULT 0
                );
            ";
            createCmd.ExecuteNonQuery();
            return;
        }

        // Transparently migrate legacy plaintext 'key' schema to 'key_hash'
        if (columns.Contains("key") && !columns.Contains("key_hash"))
        {
            _logger.LogInformation("Migrating legacy plaintext api_keys table to SHA-256 key_hash schema...");
            var legacyRows = new List<(string Id, string Name, string RawKey, string MaskedKey, string CreatedAt, string? LastUsedAt, long Prompt, long Comp, long Total, long Reqs)>();

            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = @"
                    SELECT id, name, key, masked_key, created_at, last_used_at,
                           prompt_tokens, completion_tokens, total_tokens, request_count
                    FROM api_keys;
                ";
                using var reader = readCmd.ExecuteReader();
                while (reader.Read())
                {
                    legacyRows.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9)
                    ));
                }
            }

            using var tx = conn.BeginTransaction();
            using (var createV2 = conn.CreateCommand())
            {
                createV2.Transaction = tx;
                createV2.CommandText = @"
                    CREATE TABLE api_keys_v2 (
                        id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        key_hash TEXT NOT NULL UNIQUE,
                        masked_key TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        last_used_at TEXT,
                        prompt_tokens INTEGER NOT NULL DEFAULT 0,
                        completion_tokens INTEGER NOT NULL DEFAULT 0,
                        total_tokens INTEGER NOT NULL DEFAULT 0,
                        request_count INTEGER NOT NULL DEFAULT 0
                    );
                ";
                createV2.ExecuteNonQuery();
            }

            foreach (var row in legacyRows)
            {
                if (string.IsNullOrWhiteSpace(row.RawKey)) continue;
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT OR IGNORE INTO api_keys_v2
                    (id, name, key_hash, masked_key, created_at, last_used_at, prompt_tokens, completion_tokens, total_tokens, request_count)
                    VALUES (@id, @name, @keyHash, @masked, @created, @lastUsed, @prompt, @comp, @total, @reqs);
                ";
                insertCmd.Parameters.AddWithValue("@id", row.Id);
                insertCmd.Parameters.AddWithValue("@name", row.Name);
                insertCmd.Parameters.AddWithValue("@keyHash", HashKey(row.RawKey));
                insertCmd.Parameters.AddWithValue("@masked", row.MaskedKey);
                insertCmd.Parameters.AddWithValue("@created", row.CreatedAt);
                insertCmd.Parameters.AddWithValue("@lastUsed", row.LastUsedAt != null ? row.LastUsedAt : (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue("@prompt", row.Prompt);
                insertCmd.Parameters.AddWithValue("@comp", row.Comp);
                insertCmd.Parameters.AddWithValue("@total", row.Total);
                insertCmd.Parameters.AddWithValue("@reqs", row.Reqs);
                insertCmd.ExecuteNonQuery();
            }

            using (var swapCmd = conn.CreateCommand())
            {
                swapCmd.Transaction = tx;
                swapCmd.CommandText = @"
                    DROP INDEX IF EXISTS idx_api_keys_key;
                    DROP TABLE api_keys;
                    ALTER TABLE api_keys_v2 RENAME TO api_keys;
                ";
                swapCmd.ExecuteNonQuery();
            }

            tx.Commit();
            _logger.LogInformation("Successfully migrated {Count} API keys to hashed storage.", legacyRows.Count);
        }
    }

    private void AutoMigrateFromJsonFiles(SqliteConnection conn, IWebHostEnvironment env)
    {
        var dbDir = Path.GetDirectoryName(_dbPath) ?? "";
        var candidates = new List<string>
        {
            Path.Combine(dbDir, "keys.json"),
            Path.Combine(env.ContentRootPath, "data", "keys.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "keys.json")
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
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
                                (id, name, key_hash, masked_key, created_at, last_used_at, prompt_tokens, completion_tokens, total_tokens, request_count)
                                VALUES (@id, @name, @keyHash, @masked, @created, @lastUsed, @prompt, @comp, @total, @reqs);
                            ";
                            insertCmd.Parameters.AddWithValue("@id", item.Id ?? Guid.NewGuid().ToString("N"));
                            insertCmd.Parameters.AddWithValue("@name", item.Name ?? "Migrated Key");
                            insertCmd.Parameters.AddWithValue("@keyHash", HashKey(item.Key));
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
                        _logger.LogInformation("Successfully migrated {Count} keys from {JsonPath} to hashed SQLite storage.", items.Count, path);
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

    public Task InitializeAsync() => Task.CompletedTask;

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

        // Constant-time comparison for static config key
        if (ApiKeyAuthMiddleware.IsMatchingSecret(providedKey, _options.ApiKey))
        {
            return new ApiKeyItem
            {
                Id = "config-static-key",
                Name = "appsettings.json Key",
                Key = string.Empty,
                LastUsedAtUtc = DateTime.UtcNow
            };
        }

        // O(1) lookup by SHA-256 hash; coalesce last_used_at writes to background flusher
        var keyHash = HashKey(providedKey);
        if (_keysByHash.TryGetValue(keyHash, out var matched))
        {
            var now = DateTime.UtcNow;
            matched.LastUsedAtUtc = now;
            _pendingLastUsed[matched.Id] = now;
            return matched;
        }

        return null;
    }

    private async Task RunLastUsedFlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                {
                    break;
                }

                await FlushPendingUpdatesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in SQLite last_used_at background flush loop");
            }
        }
    }

    public async Task FlushPendingUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingLastUsed.IsEmpty) return;

        var batch = new List<KeyValuePair<string, DateTime>>();
        foreach (var kvp in _pendingLastUsed)
        {
            if (_pendingLastUsed.TryRemove(kvp.Key, out var ts))
            {
                batch.Add(new KeyValuePair<string, DateTime>(kvp.Key, ts));
            }
        }

        if (batch.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var conn = await OpenConnectionAsync(cancellationToken);
            using var tx = conn.BeginTransaction();
            foreach (var (id, lastUsed) in batch)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE api_keys SET last_used_at = @lastUsed WHERE id = @id;";
                cmd.Parameters.AddWithValue("@lastUsed", lastUsed.ToString("o"));
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            tx.Commit();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to flush {Count} coalesced last_used_at updates to SQLite", batch.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = await OpenConnectionAsync(cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result) == 1L;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite health check failed");
            return false;
        }
    }

    public async Task<ApiKeyItem> CreateKeyAsync(string name)
    {
        var keyBytes = new byte[24];
        RandomNumberGenerator.Fill(keyBytes);
        var generatedKey = "agy-live-" + Convert.ToHexString(keyBytes).ToLowerInvariant();
        var keyHash = HashKey(generatedKey);
        var maskedKey = $"{generatedKey[..10]}...{generatedKey[^4..]}";
        var id = Guid.NewGuid().ToString("N");
        var cleanName = string.IsNullOrWhiteSpace(name) ? "API Key" : name.Trim();
        var createdAt = DateTime.UtcNow;

        await _writeLock.WaitAsync();
        try
        {
            using var conn = await OpenConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO api_keys
                (id, name, key_hash, masked_key, created_at, last_used_at, prompt_tokens, completion_tokens, total_tokens, request_count)
                VALUES (@id, @name, @keyHash, @masked, @created, @lastUsed, 0, 0, 0, 0);
            ";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", cleanName);
            cmd.Parameters.AddWithValue("@keyHash", keyHash);
            cmd.Parameters.AddWithValue("@masked", maskedKey);
            cmd.Parameters.AddWithValue("@created", createdAt.ToString("o"));
            cmd.Parameters.AddWithValue("@lastUsed", DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            var cachedItem = new ApiKeyItem
            {
                Id = id,
                Name = cleanName,
                Key = string.Empty,
                MaskedKey = maskedKey,
                CreatedAtUtc = createdAt,
                LastUsedAtUtc = null
            };

            _keysById[id] = cachedItem;
            _keysByHash[keyHash] = cachedItem;
            _idToHash[id] = keyHash;

            _logger.LogInformation("Created new hashed SQLite API key: {Name} ({Masked})", cleanName, maskedKey);

            // Return one-time plaintext secret to caller without caching it in memory
            return new ApiKeyItem
            {
                Id = id,
                Name = cleanName,
                Key = generatedKey,
                MaskedKey = maskedKey,
                CreatedAtUtc = createdAt,
                LastUsedAtUtc = null
            };
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
                Key = string.Empty,
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
                var now = DateTime.UtcNow;

                using var conn = await OpenConnectionAsync();
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
                cmd.Parameters.AddWithValue("@lastUsed", now.ToString("o"));
                cmd.Parameters.AddWithValue("@id", keyId);
                await cmd.ExecuteNonQueryAsync();

                // Update in-memory counters only after SQLite write succeeds
                matched.PromptTokens += promptTokens;
                matched.CompletionTokens += completionTokens;
                matched.TotalTokens += totalTokens;
                matched.RequestCount++;
                matched.LastUsedAtUtc = now;
                _pendingLastUsed.TryRemove(keyId, out _);

                _logger.LogDebug("SQLite: updated token usage for key {Id} ({Name}): +{Prompt} prompt, +{Completion} completion, +{Total} total",
                    matched.Id, matched.Name, promptTokens, completionTokens, totalTokens);
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
            await _writeLock.WaitAsync();
            try
            {
                using var conn = await OpenConnectionAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE api_keys
                    SET prompt_tokens = 0, completion_tokens = 0, total_tokens = 0, request_count = 0
                    WHERE id = @id;
                ";
                cmd.Parameters.AddWithValue("@id", id);
                var rows = await cmd.ExecuteNonQueryAsync();

                if (rows > 0)
                {
                    matched.PromptTokens = 0;
                    matched.CompletionTokens = 0;
                    matched.TotalTokens = 0;
                    matched.RequestCount = 0;
                    _logger.LogInformation("SQLite: reset token usage counters for key {Id} ({Name}) to 0", matched.Id, matched.Name);
                }

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
            using var conn = await OpenConnectionAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM api_keys WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            var rows = await cmd.ExecuteNonQueryAsync();

            _keysById.TryRemove(id, out _);
            _pendingLastUsed.TryRemove(id, out _);
            if (_idToHash.TryRemove(id, out var keyHash))
            {
                _keysByHash.TryRemove(keyHash, out _);
            }

            _logger.LogInformation("SQLite: revoked API key with ID {Id}", id);
            return rows > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _flushCts.Cancel();
        try
        {
            await _flushTask;
        }
        catch
        {
            // ignored
        }

        try
        {
            await FlushPendingUpdatesAsync(CancellationToken.None);
        }
        catch
        {
            // ignored
        }

        _flushCts.Dispose();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
