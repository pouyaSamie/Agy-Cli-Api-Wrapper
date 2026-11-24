using AgyApi.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Agy;

public class AgyWorkerPool : IAgyWorkerPool
{
    private readonly IOptions<AgyOptions> _options;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgyWorkerPool> _logger;
    private readonly Func<string, string, string, string?, bool, IAgyWorker> _workerFactory;

    private readonly List<IAgyWorker> _workers = new();
    private readonly HashSet<string> _reservedWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sessionWorkerMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _workerSessionMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly PeriodicTimer? _maintenanceTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _maintenanceTask;
    private TaskCompletionSource<bool> _poolChangedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingSpawns;
    private int _workerSequence;
    private volatile bool _disposed;

    [ActivatorUtilitiesConstructor]
    public AgyWorkerPool(
        IOptions<AgyOptions> options,
        IAgyTranscriptReader transcriptReader,
        ILoggerFactory loggerFactory,
        ILogger<AgyWorkerPool> logger)
        : this(options, transcriptReader, loggerFactory, logger, workerFactory: (Func<string, string, string, string?, bool, IAgyWorker>?)null, startMaintenanceLoop: true)
    {
    }

    public AgyWorkerPool(
        IOptions<AgyOptions> options,
        IAgyTranscriptReader transcriptReader,
        ILoggerFactory loggerFactory,
        ILogger<AgyWorkerPool> logger,
        Func<string, string, string, string?, IAgyWorker>? workerFactory,
        bool startMaintenanceLoop = true)
        : this(
            options,
            transcriptReader,
            loggerFactory,
            logger,
            workerFactory != null ? (id, model, effort, convId, _) => workerFactory(id, model, effort, convId) : null,
            startMaintenanceLoop)
    {
    }

    public AgyWorkerPool(
        IOptions<AgyOptions> options,
        IAgyTranscriptReader transcriptReader,
        ILoggerFactory loggerFactory,
        ILogger<AgyWorkerPool> logger,
        Func<string, string, string, string?, bool, IAgyWorker>? workerFactory,
        bool startMaintenanceLoop = true)
    {
        _options = options;
        _transcriptReader = transcriptReader;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _workerFactory = workerFactory ?? DefaultWorkerFactory;

        if (startMaintenanceLoop)
        {
            _maintenanceTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            _maintenanceTask = RunMaintenanceLoopAsync(_cts.Token);
        }
        else
        {
            _maintenanceTask = Task.CompletedTask;
        }
    }

    private IAgyWorker DefaultWorkerFactory(string id, string model, string effort, string? conversationId, bool enableAgentic)
    {
        return new AgyWorker(
            id,
            model,
            effort,
            conversationId,
            _options,
            _transcriptReader,
            _loggerFactory.CreateLogger<AgyWorker>(),
            enableAgentic);
    }

    public async Task<IAgyWorker> AcquireWorkerAsync(
        string? sessionId,
        string model,
        string effort,
        string? conversationId = null,
        CancellationToken cancellationToken = default,
        bool enableAgentic = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveAgentic = enableAgentic && _options.Value.AllowAgenticTools;
        var acquireTimeoutSeconds = Math.Max(1, _options.Value.PoolAcquireTimeoutSeconds);
        var deadlineUtc = DateTime.UtcNow.AddSeconds(acquireTimeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IAgyWorker? staleWorkerToTerminate = null;
            IAgyWorker? evictedWorker = null;
            Task? waitTask = null;

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                // 1. Session Affinity: Check if this session is already bound to a healthy matching worker
                if (!string.IsNullOrWhiteSpace(sessionId) &&
                    _sessionWorkerMap.TryGetValue(sessionId, out var boundWorkerId))
                {
                    var candidate = _workers.FirstOrDefault(w => string.Equals(w.Id, boundWorkerId, StringComparison.OrdinalIgnoreCase));
                    if (candidate != null &&
                        candidate.IsHealthy &&
                        candidate.EnableAgenticMode == effectiveAgentic &&
                        string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.Effort, effort, StringComparison.OrdinalIgnoreCase) &&
                        candidate.TurnCount < _options.Value.MaxTurnsPerWorker)
                    {
                        if (!_reservedWorkers.Contains(candidate.Id) && !candidate.IsBusy)
                        {
                            _reservedWorkers.Add(candidate.Id);
                            _logger.LogInformation("Session affinity hit: session {SessionId} routed to warm worker {WorkerId} (PID {Pid})",
                                sessionId, candidate.Id, candidate.ProcessId);
                            return candidate;
                        }

                        // Bound worker is busy with the same session; wait until released
                        waitTask = _poolChangedTcs.Task;
                    }
                    else
                    {
                        // Bound worker is dead, exhausted, or changed model/effort/agentic mode: unbind and retire if unreserved
                        _sessionWorkerMap.Remove(sessionId);
                        if (candidate != null)
                        {
                            UnbindWorkerSessionsLocked(candidate.Id);
                            if (!_reservedWorkers.Contains(candidate.Id) && !candidate.IsBusy)
                            {
                                _workers.Remove(candidate);
                                staleWorkerToTerminate = candidate;
                                SignalPoolChangedLocked();
                            }
                        }
                    }
                }

                if (waitTask == null)
                {
                    // 2. Clean Warm Standby Reuse: only reuse unused (TurnCount == 0, unbound) matching workers,
                    //    and never skip spawning when explicitly resuming an existing conversationId.
                    if (string.IsNullOrWhiteSpace(conversationId))
                    {
                        var cleanIdle = _workers
                            .Where(w => !_reservedWorkers.Contains(w.Id) &&
                                        !w.IsBusy &&
                                        w.IsHealthy &&
                                        w.TurnCount == 0 &&
                                        w.EnableAgenticMode == effectiveAgentic &&
                                        !_workerSessionMap.ContainsKey(w.Id) &&
                                        string.Equals(w.Model, model, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(w.Effort, effort, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(w => w.LastActiveUtc)
                            .FirstOrDefault();

                        if (cleanIdle != null)
                        {
                            _reservedWorkers.Add(cleanIdle.Id);
                            if (!string.IsNullOrWhiteSpace(sessionId))
                            {
                                BindSessionLocked(sessionId, cleanIdle.Id);
                            }
                            _logger.LogInformation("Assigned clean standby worker {WorkerId} (PID {Pid}) to session {SessionId}",
                                cleanIdle.Id, cleanIdle.ProcessId, sessionId ?? "ephemeral");
                            return cleanIdle;
                        }
                    }

                    // 3. Enforce MaxPoolSize: evict an unreserved idle worker (preferring unhealthy/oldest) or wait
                    var maxPool = Math.Max(1, _options.Value.MaxPoolSize);
                    if (_workers.Count + _pendingSpawns >= maxPool)
                    {
                        var evictCandidate = _workers
                            .Where(w => !_reservedWorkers.Contains(w.Id) && !w.IsBusy)
                            .OrderBy(w => w.IsHealthy ? 1 : 0)
                            .ThenBy(w => w.LastActiveUtc)
                            .FirstOrDefault();

                        if (evictCandidate != null)
                        {
                            _logger.LogInformation("Evicting idle worker {WorkerId} (PID {Pid}) to satisfy pool capacity limit ({Max})",
                                evictCandidate.Id, evictCandidate.ProcessId, maxPool);
                            _workers.Remove(evictCandidate);
                            UnbindWorkerSessionsLocked(evictCandidate.Id);
                            evictedWorker = evictCandidate;
                            SignalPoolChangedLocked();
                        }
                        else
                        {
                            waitTask = _poolChangedTcs.Task;
                        }
                    }

                    if (waitTask == null)
                    {
                        _pendingSpawns++;
                    }
                }
            }

            if (staleWorkerToTerminate != null)
            {
                TerminateWorkerInBackground(staleWorkerToTerminate);
            }

            if (evictedWorker != null)
            {
                TerminateWorkerInBackground(evictedWorker);
            }

            if (waitTask != null)
            {
                var remaining = deadlineUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out after {acquireTimeoutSeconds}s waiting for an available AGY worker (MaxPoolSize={_options.Value.MaxPoolSize}).");
                }

                await waitTask.WaitAsync(remaining, cancellationToken);
                continue;
            }

            // 4. Spawn and initialize a new worker outside the pool lock
            var newId = $"worker-{Interlocked.Increment(ref _workerSequence)}";
            var newWorker = _workerFactory(newId, model, effort, conversationId, effectiveAgentic);

            try
            {
                await newWorker.InitializeAsync(cancellationToken);
            }
            catch
            {
                lock (_lock)
                {
                    _pendingSpawns--;
                    SignalPoolChangedLocked();
                }
                throw;
            }

            bool poolDisposedDuringInit = false;
            lock (_lock)
            {
                _pendingSpawns--;
                if (_disposed)
                {
                    poolDisposedDuringInit = true;
                }
                else
                {
                    _workers.Add(newWorker);
                    _reservedWorkers.Add(newWorker.Id);
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        BindSessionLocked(sessionId, newWorker.Id);
                    }
                    SignalPoolChangedLocked();
                }
            }

            if (poolDisposedDuringInit)
            {
                await SafeTerminateAsync(newWorker);
                throw new ObjectDisposedException(nameof(AgyWorkerPool));
            }

            _logger.LogInformation("Spawned new worker {WorkerId} (PID {Pid}) for model {Model}, effort {Effort}, agentic {Agentic}, conversation {Conv}",
                newWorker.Id, newWorker.ProcessId, model, effort, effectiveAgentic, conversationId ?? "new");

            return newWorker;
        }
    }

    public void ReleaseWorker(IAgyWorker worker)
    {
        IAgyWorker? toTerminate = null;

        lock (_lock)
        {
            _reservedWorkers.Remove(worker.Id);

            var isBoundToSession = _workerSessionMap.ContainsKey(worker.Id);
            var shouldRetire = _disposed ||
                               !worker.IsHealthy ||
                               worker.TurnCount >= _options.Value.MaxTurnsPerWorker ||
                               (worker.TurnCount > 0 && !isBoundToSession);

            if (shouldRetire)
            {
                _logger.LogInformation("Retiring worker {WorkerId} (turns: {Turns}, healthy: {Healthy}, bound: {Bound})",
                    worker.Id, worker.TurnCount, worker.IsHealthy, isBoundToSession);
                _workers.Remove(worker);
                UnbindWorkerSessionsLocked(worker.Id);
                toTerminate = worker;
            }

            SignalPoolChangedLocked();
        }

        if (toTerminate != null)
        {
            TerminateWorkerInBackground(toTerminate);
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Value.EnableWorkerPool || _disposed) return;

        var maxPool = Math.Max(1, _options.Value.MaxPoolSize);
        var targetWarm = Math.Min(Math.Max(0, _options.Value.WarmWorkersOnStartup), maxPool);

        for (int i = 0; i < targetWarm; i++)
        {
            if (cancellationToken.IsCancellationRequested || _disposed) break;

            bool reservedSlot = false;
            lock (_lock)
            {
                if (_disposed) break;
                var cleanStandbyCount = _workers.Count(w =>
                    w.IsHealthy &&
                    w.TurnCount == 0 &&
                    !w.EnableAgenticMode &&
                    !_workerSessionMap.ContainsKey(w.Id) &&
                    !_reservedWorkers.Contains(w.Id));

                if (cleanStandbyCount < targetWarm && (_workers.Count + _pendingSpawns) < maxPool)
                {
                    _pendingSpawns++;
                    reservedSlot = true;
                }
            }

            if (!reservedSlot) break;

            await SpawnStandbyWorkerAsync(cancellationToken);
        }
    }

    public async Task RecycleAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Recycling all workers in AgyWorkerPool...");
        List<IAgyWorker> toRecycle;
        lock (_lock)
        {
            toRecycle = new List<IAgyWorker>(_workers);
            _workers.Clear();
            _reservedWorkers.Clear();
            _sessionWorkerMap.Clear();
            _workerSessionMap.Clear();
            SignalPoolChangedLocked();
        }

        foreach (var worker in toRecycle)
        {
            await SafeTerminateAsync(worker);
        }

        await WarmUpAsync(cancellationToken);

        int activeCount;
        lock (_lock)
        {
            activeCount = _workers.Count;
        }
        _logger.LogInformation("Worker pool recycle completed. Active warm workers: {Count}", activeCount);
    }

    public AgyPoolStats GetStats()
    {
        List<(IAgyWorker Worker, bool IsReserved)> snapshot;
        lock (_lock)
        {
            snapshot = _workers
                .Select(w => (w, _reservedWorkers.Contains(w.Id)))
                .ToList();
        }

        // Perform OS process memory queries (WorkingSetBytes) outside the pool lock
        var infos = snapshot.Select(entry =>
        {
            var w = entry.Worker;
            return new AgyWorkerInfo(
                w.Id,
                w.ProcessId,
                w.Model,
                w.Effort,
                w.CurrentConversationId,
                w.TurnCount,
                w.IsBusy || entry.IsReserved,
                w.IsHealthy,
                w.WorkingSetBytes,
                w.CreatedUtc,
                w.LastActiveUtc
            );
        }).ToList();

        var active = infos.Count(w => w.IsBusy);
        var idle = infos.Count(w => !w.IsBusy);
        var totalTurns = infos.Sum(w => w.TurnCount);
        var totalBytes = infos.Sum(w => w.WorkingSetBytes);

        return new AgyPoolStats(
            TotalWorkers: infos.Count,
            ActiveWorkers: active,
            IdleWorkers: idle,
            TotalTurnsCompleted: totalTurns,
            TotalWorkingSetBytes: totalBytes,
            Workers: infos
        );
    }

    private async Task SpawnStandbyWorkerAsync(CancellationToken ct)
    {
        try
        {
            var newId = $"worker-{Interlocked.Increment(ref _workerSequence)}";
            var worker = _workerFactory(
                newId,
                _options.Value.DefaultModel,
                _options.Value.DefaultReasoningEffort,
                null,
                false);

            await worker.InitializeAsync(ct);

            bool disposedDuringInit = false;
            lock (_lock)
            {
                _pendingSpawns--;
                if (_disposed)
                {
                    disposedDuringInit = true;
                }
                else
                {
                    _workers.Add(worker);
                    SignalPoolChangedLocked();
                }
            }

            if (disposedDuringInit)
            {
                await SafeTerminateAsync(worker);
                return;
            }

            _logger.LogInformation("Warmed up standby worker {WorkerId} (PID {Pid})", worker.Id, worker.ProcessId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                _pendingSpawns--;
                SignalPoolChangedLocked();
            }
            _logger.LogWarning(ex, "Failed to spawn standby worker");
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _pendingSpawns--;
                SignalPoolChangedLocked();
            }
        }
    }

    private void TerminateWorkerInBackground(IAgyWorker worker)
    {
        _ = Task.Run(() => SafeTerminateAsync(worker));
    }

    private async Task SafeTerminateAsync(IAgyWorker worker)
    {
        try
        {
            await worker.TerminateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error terminating worker {WorkerId}", worker.Id);
        }
    }

    private void BindSessionLocked(string sessionId, string workerId)
    {
        if (_sessionWorkerMap.TryGetValue(sessionId, out var prevWorkerId) &&
            !string.Equals(prevWorkerId, workerId, StringComparison.OrdinalIgnoreCase))
        {
            _workerSessionMap.Remove(prevWorkerId);
        }

        if (_workerSessionMap.TryGetValue(workerId, out var prevSessionId) &&
            !string.Equals(prevSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            _sessionWorkerMap.Remove(prevSessionId);
        }

        _sessionWorkerMap[sessionId] = workerId;
        _workerSessionMap[workerId] = sessionId;
    }

    private void UnbindWorkerSessionsLocked(string workerId)
    {
        if (_workerSessionMap.TryGetValue(workerId, out var boundSession))
        {
            _workerSessionMap.Remove(workerId);
            _sessionWorkerMap.Remove(boundSession);
        }

        var keysToRemove = _sessionWorkerMap
            .Where(kvp => string.Equals(kvp.Value, workerId, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _sessionWorkerMap.Remove(key);
        }
    }

    private void SignalPoolChangedLocked()
    {
        var previous = _poolChangedTcs;
        _poolChangedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult(true);
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken ct)
    {
        if (_maintenanceTimer == null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _maintenanceTimer.WaitForNextTickAsync(ct);

                var idleTimeout = TimeSpan.FromMinutes(_options.Value.WorkerIdleTimeoutMinutes);
                var now = DateTime.UtcNow;
                var toRetire = new List<IAgyWorker>();
                bool shouldWarmStandby = false;

                lock (_lock)
                {
                    if (_disposed) break;

                    foreach (var worker in _workers.ToList())
                    {
                        if (_reservedWorkers.Contains(worker.Id) || worker.IsBusy)
                        {
                            continue;
                        }

                        if (!worker.IsHealthy)
                        {
                            _logger.LogWarning("Maintenance: pruning dead worker {WorkerId}", worker.Id);
                            _workers.Remove(worker);
                            UnbindWorkerSessionsLocked(worker.Id);
                            toRetire.Add(worker);
                        }
                        else if ((now - worker.LastActiveUtc) > idleTimeout)
                        {
                            if (worker.TurnCount > 0 || _workers.Count > _options.Value.WarmWorkersOnStartup)
                            {
                                _logger.LogInformation("Maintenance: retiring idle worker {WorkerId} (idle for {Mins:F1} mins)",
                                    worker.Id, (now - worker.LastActiveUtc).TotalMinutes);
                                _workers.Remove(worker);
                                UnbindWorkerSessionsLocked(worker.Id);
                                toRetire.Add(worker);
                            }
                        }
                    }

                    if (toRetire.Count > 0)
                    {
                        SignalPoolChangedLocked();
                    }

                    var maxPool = Math.Max(1, _options.Value.MaxPoolSize);
                    var cleanStandbyCount = _workers.Count(w =>
                        w.IsHealthy &&
                        w.TurnCount == 0 &&
                        !w.EnableAgenticMode &&
                        !_workerSessionMap.ContainsKey(w.Id) &&
                        !_reservedWorkers.Contains(w.Id));

                    if (cleanStandbyCount < _options.Value.WarmWorkersOnStartup &&
                        (_workers.Count + _pendingSpawns) < maxPool &&
                        !ct.IsCancellationRequested)
                    {
                        _pendingSpawns++;
                        shouldWarmStandby = true;
                    }
                }

                foreach (var worker in toRetire)
                {
                    await SafeTerminateAsync(worker);
                }

                if (shouldWarmStandby && !ct.IsCancellationRequested)
                {
                    await SpawnStandbyWorkerAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AgyWorkerPool maintenance loop");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        List<IAgyWorker> toDispose;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = new List<IAgyWorker>(_workers);
            _workers.Clear();
            _reservedWorkers.Clear();
            _sessionWorkerMap.Clear();
            _workerSessionMap.Clear();
            _poolChangedTcs.TrySetCanceled();
        }

        _cts.Cancel();
        _maintenanceTimer?.Dispose();

        try
        {
            await _maintenanceTask;
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();

        foreach (var worker in toDispose)
        {
            await SafeTerminateAsync(worker);
        }

        GC.SuppressFinalize(this);
    }
}
