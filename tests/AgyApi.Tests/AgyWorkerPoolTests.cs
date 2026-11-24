using System.Runtime.CompilerServices;
using AgyApi.Configuration;
using AgyApi.Infrastructure.Agy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgyApi.Tests;

public class AgyWorkerPoolTests
{
    private class DummyTranscriptReader : IAgyTranscriptReader
    {
        public Task<string?> ExtractThinkingAsync(string conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("dummy thinking");

        public Task<string?> ExtractLatestResponseAsync(string conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("dummy response");
    }

    private sealed class FakeAgyWorker : IAgyWorker
    {
        private readonly Func<CancellationToken, Task>? _onInitialize;

        public FakeAgyWorker(
            string id,
            string model,
            string effort,
            string? initialConversationId,
            bool enableAgentic = false,
            Func<CancellationToken, Task>? onInitialize = null)
        {
            Id = id;
            Model = model;
            Effort = effort;
            InitialConversationId = initialConversationId;
            CurrentConversationId = initialConversationId ?? $"conv-{id}";
            EnableAgenticMode = enableAgentic;
            _onInitialize = onInitialize;
            CreatedUtc = DateTime.UtcNow;
            LastActiveUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public int ProcessId => 1000;
        public string? CurrentConversationId { get; }
        public string Model { get; }
        public string Effort { get; }
        public bool EnableAgenticMode { get; }
        public int TurnCount { get; set; }
        public DateTime CreatedUtc { get; }
        public DateTime LastActiveUtc { get; private set; }
        public long WorkingSetBytes => 1024;
        public bool IsHealthy { get; set; } = true;
        public bool IsBusy { get; set; }
        public bool Terminated { get; private set; }
        public string? InitialConversationId { get; }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_onInitialize != null)
            {
                await _onInitialize(cancellationToken);
            }
            IsHealthy = true;
        }

        public async IAsyncEnumerable<AgyStreamItem> ExecuteTurnStreamAsync(
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IsBusy = true;
            try
            {
                await Task.Yield();
                TurnCount++;
                LastActiveUtc = DateTime.UtcNow;
                yield return new AgyStreamItem
                {
                    ConversationId = CurrentConversationId,
                    IsComplete = true,
                    FullResponse = $"echo:{prompt}"
                };
            }
            finally
            {
                IsBusy = false;
            }
        }

        public Task TerminateAsync()
        {
            Terminated = true;
            IsHealthy = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(TerminateAsync());
        }
    }

    [Fact]
    public async Task GetStats_Initially_ReflectsEmptyOrConfiguredPool()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0
        });

        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (Func<string, string, string, string?, bool, IAgyWorker>?)null,
            startMaintenanceLoop: false);

        var stats = pool.GetStats();

        Assert.Equal(0, stats.TotalWorkers);
        Assert.Equal(0, stats.ActiveWorkers);
        Assert.Equal(0, stats.IdleWorkers);
        Assert.Equal(0, stats.TotalTurnsCompleted);
        Assert.Equal(0, stats.TotalWorkingSetBytes);
        Assert.Empty(stats.Workers);
    }

    [Fact]
    public async Task ReleaseWorker_WhenWorkerUnhealthy_RetiresAndTerminatesWorkerFromPool()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0
        });

        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) => new FakeAgyWorker(id, model, effort, convId, agentic),
            startMaintenanceLoop: false);

        var worker = (FakeAgyWorker)await pool.AcquireWorkerAsync("sess-1", "gemini-3.8-flash-medium", "medium");
        Assert.Equal(1, pool.GetStats().TotalWorkers);

        // Simulate worker crash / cancellation marking it unhealthy
        worker.IsHealthy = false;
        pool.ReleaseWorker(worker);

        // Give background termination a moment to run
        for (int i = 0; i < 20 && !worker.Terminated; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(worker.Terminated);
        Assert.Equal(0, pool.GetStats().TotalWorkers);
    }

    [Fact]
    public void Deserialize_AgyResultEvent_SuccessfullyPopulates()
    {
        var json = "{\"event\":\"result\",\"result\":{\"conversation_id\":\"123\",\"status\":\"SUCCESS\",\"response\":\"hello\"}}";
        var ev = System.Text.Json.JsonSerializer.Deserialize<AgyRawEvent>(json);
        Assert.NotNull(ev);
        Assert.Equal("result", ev.Event);
        Assert.NotNull(ev.Result);
        Assert.Equal("hello", ev.Result.Response);
    }

    [Fact]
    public async Task AcquireWorkerAsync_NeverReusesDirtyWorkerAcrossDifferentSessions()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0
        });

        var createdWorkers = new List<FakeAgyWorker>();
        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) =>
            {
                var w = new FakeAgyWorker(id, model, effort, convId, agentic);
                createdWorkers.Add(w);
                return w;
            },
            startMaintenanceLoop: false);

        var workerA1 = await pool.AcquireWorkerAsync("session-a", "gemini-3.8-flash-medium", "medium");
        await foreach (var _ in workerA1.ExecuteTurnStreamAsync("secret ALPHA99")) { }
        pool.ReleaseWorker(workerA1);

        // Same session should hit affinity on the same bound worker
        var workerA2 = await pool.AcquireWorkerAsync("session-a", "gemini-3.8-flash-medium", "medium");
        Assert.Same(workerA1, workerA2);
        pool.ReleaseWorker(workerA2);

        // Different session MUST NOT get session-a's dirty worker
        var workerB = await pool.AcquireWorkerAsync("session-b", "gemini-3.8-flash-medium", "medium");
        Assert.NotSame(workerA1, workerB);
        Assert.Equal(2, createdWorkers.Count);
        pool.ReleaseWorker(workerB);
    }

    [Fact]
    public async Task AcquireWorkerAsync_WithConversationId_SpawnsWorkerWithConversationId()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0
        });

        var capturedConvIds = new List<string?>();
        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) =>
            {
                capturedConvIds.Add(convId);
                return new FakeAgyWorker(id, model, effort, convId, agentic);
            },
            startMaintenanceLoop: false);

        var clean = await pool.AcquireWorkerAsync(sessionId: null, "gemini-3.8-flash-medium", "medium");
        pool.ReleaseWorker(clean);

        var resumed = await pool.AcquireWorkerAsync("session-resume", "gemini-3.8-flash-medium", "medium", conversationId: "existing-conv-42");
        Assert.NotSame(clean, resumed);
        Assert.Equal(new[] { null, "existing-conv-42" }, capturedConvIds);
        pool.ReleaseWorker(resumed);
    }

    [Fact]
    public async Task AcquireWorkerAsync_ConcurrentCalls_DoNotReceiveSameIdleWorker()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 2,
            WarmWorkersOnStartup = 0
        });

        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) => new FakeAgyWorker(id, model, effort, convId, agentic),
            startMaintenanceLoop: false);

        var initial = await pool.AcquireWorkerAsync(null, "gemini-3.8-flash-medium", "medium");
        pool.ReleaseWorker(initial);

        var t1 = pool.AcquireWorkerAsync("s1", "gemini-3.8-flash-medium", "medium");
        var t2 = pool.AcquireWorkerAsync("s2", "gemini-3.8-flash-medium", "medium");
        var workers = await Task.WhenAll(t1, t2);

        Assert.NotSame(workers[0], workers[1]);
        pool.ReleaseWorker(workers[0]);
        pool.ReleaseWorker(workers[1]);
    }

    [Fact]
    public async Task AcquireWorkerAsync_EnforcesMaxPoolSize_WaitsForRelease()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 1,
            WarmWorkersOnStartup = 0
        });

        var spawnCount = 0;
        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) =>
            {
                Interlocked.Increment(ref spawnCount);
                return new FakeAgyWorker(id, model, effort, convId, agentic);
            },
            startMaintenanceLoop: false);

        var w1 = await pool.AcquireWorkerAsync("s1", "gemini-3.8-flash-medium", "medium");
        await foreach (var _ in w1.ExecuteTurnStreamAsync("hello")) { }

        var acquireS2Task = pool.AcquireWorkerAsync("s2", "gemini-3.8-flash-medium", "medium");
        await Task.Delay(50);
        Assert.False(acquireS2Task.IsCompleted);
        Assert.Equal(1, spawnCount);

        pool.ReleaseWorker(w1);
        var w2 = await acquireS2Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotSame(w1, w2);
        Assert.Equal(2, spawnCount);

        for (int i = 0; i < 20 && !((FakeAgyWorker)w1).Terminated; i++)
        {
            await Task.Delay(10);
        }
        Assert.True(((FakeAgyWorker)w1).Terminated);
        pool.ReleaseWorker(w2);
    }

    [Fact]
    public async Task AcquireWorkerAsync_SegregatesAgenticAndNonAgenticWorkers()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            AllowAgenticTools = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0
        });

        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) => new FakeAgyWorker(id, model, effort, convId, agentic),
            startMaintenanceLoop: false);

        // Create a clean non-agentic standby
        var nonAgentic = await pool.AcquireWorkerAsync(null, "gemini-3.8-flash-medium", "medium", enableAgentic: false);
        pool.ReleaseWorker(nonAgentic);
        Assert.False(nonAgentic.EnableAgenticMode);

        // Requesting an agentic worker MUST NOT reuse the non-agentic standby
        var agentic = await pool.AcquireWorkerAsync("s-agent", "gemini-3.8-flash-medium", "medium", enableAgentic: true);
        Assert.NotSame(nonAgentic, agentic);
        Assert.True(agentic.EnableAgenticMode);
        pool.ReleaseWorker(agentic);
    }

    [Fact]
    public async Task AcquireWorkerAsync_WhenDisposedDuringInitialize_TerminatesOrphanAndThrows()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 2,
            WarmWorkersOnStartup = 0
        });

        var initStarted = new TaskCompletionSource<bool>();
        var allowInitFinish = new TaskCompletionSource<bool>();
        FakeAgyWorker? spawned = null;

        var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) =>
            {
                spawned = new FakeAgyWorker(id, model, effort, convId, agentic, async _ =>
                {
                    initStarted.TrySetResult(true);
                    await allowInitFinish.Task;
                });
                return spawned;
            },
            startMaintenanceLoop: false);

        var acquireTask = pool.AcquireWorkerAsync("s-dispose", "gemini-3.8-flash-medium", "medium");
        await initStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Dispose pool while worker is still initializing
        await pool.DisposeAsync();
        allowInitFinish.TrySetResult(true);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquireTask);
        Assert.NotNull(spawned);
        Assert.True(spawned!.Terminated);
        Assert.Equal(0, pool.GetStats().TotalWorkers);
    }

    [Fact]
    public async Task AcquireWorkerAsync_WhenPoolSaturatedPastTimeout_ThrowsTimeoutException()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 1,
            WarmWorkersOnStartup = 0,
            PoolAcquireTimeoutSeconds = 1
        });

        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) => new FakeAgyWorker(id, model, effort, convId, agentic),
            startMaintenanceLoop: false);

        var w1 = await pool.AcquireWorkerAsync("s1", "gemini-3.8-flash-medium", "medium");
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                pool.AcquireWorkerAsync("s2", "gemini-3.8-flash-medium", "medium"));
        }
        finally
        {
            pool.ReleaseWorker(w1);
        }
    }

    [Fact]
    public async Task ReleaseWorker_EphemeralDirtyWorker_RetiresAndTerminates()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 2,
            WarmWorkersOnStartup = 0
        });

        await using var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance,
            (id, model, effort, convId, agentic) => new FakeAgyWorker(id, model, effort, convId, agentic),
            startMaintenanceLoop: false);

        var w = (FakeAgyWorker)await pool.AcquireWorkerAsync(null, "gemini-3.8-flash-medium", "medium");
        await foreach (var _ in w.ExecuteTurnStreamAsync("ephemeral")) { }
        pool.ReleaseWorker(w);

        for (int i = 0; i < 20 && !w.Terminated; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(w.Terminated);
        Assert.Equal(0, pool.GetStats().TotalWorkers);
    }

    [Fact]
    public async Task KeyedAsyncLock_EvictsKeysAfterRelease()
    {
        var keyedLock = new KeyedAsyncLock();
        using (await keyedLock.AcquireAsync("conv-a"))
        {
            Assert.Equal(1, keyedLock.ActiveKeyCount);
        }

        Assert.Equal(0, keyedLock.ActiveKeyCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AgyWorker_RealProcess_InvalidModel_ThrowsAndRemainsUnhealthy()
    {
        var options = Options.Create(new AgyOptions
        {
            ExecutablePath = "agy",
            DefaultModel = "gemini-3.8-flash-medium",
            DefaultReasoningEffort = "medium",
            WorkspaceDirectory = "./workspace"
        });

        var worker = new AgyWorker(
            "test-worker-invalid",
            "invalid-model-does-not-exist",
            "medium",
            conversationId: null,
            options,
            new DummyTranscriptReader(),
            NullLogger<AgyWorker>.Instance);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => worker.InitializeAsync());
        Assert.False(worker.IsHealthy);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        await worker.TerminateAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AgyWorker_RealProcess_InitializesAndExecutesTurn()
    {
        var options = Options.Create(new AgyOptions
        {
            ExecutablePath = "agy",
            DefaultModel = "gemini-3.8-flash-medium",
            DefaultReasoningEffort = "medium",
            WorkspaceDirectory = "./workspace"
        });

        var worker = new AgyWorker(
            "test-worker-1",
            "gemini-3.8-flash-medium",
            "medium",
            conversationId: null,
            options,
            new DummyTranscriptReader(),
            NullLogger<AgyWorker>.Instance);

        await worker.InitializeAsync();
        try
        {
            Assert.True(worker.IsHealthy);
            Assert.True(worker.ProcessId > 0);

            var items = new List<AgyStreamItem>();
            await foreach (var item in worker.ExecuteTurnStreamAsync("Say the word PING only"))
            {
                items.Add(item);
            }

            Assert.NotEmpty(items);
            var completed = items.FirstOrDefault(i => i.IsComplete);
            Assert.NotNull(completed);
            Assert.False(string.IsNullOrEmpty(completed.FullResponse));
            Assert.True(worker.IsHealthy);
        }
        finally
        {
            await worker.TerminateAsync();
        }
    }
}
