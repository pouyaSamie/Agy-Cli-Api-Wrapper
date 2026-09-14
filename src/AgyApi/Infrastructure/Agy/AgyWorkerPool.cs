using System.Collections.Concurrent;
using AgyApi.Configuration;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Agy;

public class AgyWorkerPool : IAgyWorkerPool
{
    private readonly IOptions<AgyOptions> _options;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgyWorkerPool> _logger;

    private readonly List<IAgyWorker> _workers = new();
    private readonly ConcurrentDictionary<string, string> _sessionWorkerMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly PeriodicTimer _maintenanceTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _maintenanceTask;
    private int _workerSequence;
    private bool _disposed;

    public AgyWorkerPool(
        IOptions<AgyOptions> options,
        IAgyTranscriptReader transcriptReader,
        ILoggerFactory loggerFactory,
        ILogger<AgyWorkerPool> logger)
    {
        _options = options;
        _transcriptReader = transcriptReader;
        _loggerFactory = loggerFactory;
        _logger = logger;

        _maintenanceTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _maintenanceTask = RunMaintenanceLoopAsync(_cts.Token);
    }

    public async Task<IAgyWorker> AcquireWorkerAsync(
        string? sessionId,
        string model,
        string effort,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // 1. Session Affinity: Check if this session is already bound to a warm worker
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                _sessionWorkerMap.TryGetValue(sessionId, out var boundWorkerId))
            {
                var candidate = _workers.FirstOrDefault(w => w.Id == boundWorkerId);
                if (candidate != null &&
                    candidate.IsHealthy &&
                    string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Effort, effort, StringComparison.OrdinalIgnoreCase) &&
                    candidate.TurnCount < _options.Value.MaxTurnsPerWorker)
                {
                    _logger.LogInformation("Session affinity hit: session {SessionId} routed to warm worker {WorkerId} (PID {Pid})",
                        sessionId, candidate.Id, candidate.ProcessId);
                    return candidate;
                }

                // If candidate is dead, full of turns, or model changed: unbind
                _sessionWorkerMap.TryRemove(sessionId, out _);
            }

            // 2. Idle Worker Selection: Find an idle worker with matching model and effort
            var idleWorker = _workers
                .Where(w => !w.IsBusy && w.IsHealthy &&
                            string.Equals(w.Model, model, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(w.Effort, effort, StringComparison.OrdinalIgnoreCase) &&
                            w.TurnCount < _options.Value.MaxTurnsPerWorker)
                .OrderBy(w => w.LastActiveUtc)
                .FirstOrDefault();

            if (idleWorker != null)
            {
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    _sessionWorkerMap[sessionId] = idleWorker.Id;
                }
                _logger.LogInformation("Assigned idle worker {WorkerId} (PID {Pid}) to session {SessionId}",
                    idleWorker.Id, idleWorker.ProcessId, sessionId ?? "ephemeral");
                return idleWorker;
            }

            // 3. Pool Capacity: If pool is at MaxPoolSize, recycle the oldest idle worker
            if (_workers.Count >= _options.Value.MaxPoolSize)
            {
                var evictCandidate = _workers
                    .Where(w => !w.IsBusy)
                    .OrderBy(w => w.LastActiveUtc)
                    .FirstOrDefault();

                if (evictCandidate != null)
                {
                    _logger.LogInformation("Evicting oldest idle worker {WorkerId} (PID {Pid}) to satisfy pool capacity limit ({Max})",
                        evictCandidate.Id, evictCandidate.ProcessId, _options.Value.MaxPoolSize);
                    _workers.Remove(evictCandidate);
                    UnbindWorkerSessions(evictCandidate.Id);
                    _ = evictCandidate.TerminateAsync();
                }
            }
        }

        // 4. Spawn a new worker
        var newId = $"worker-{Interlocked.Increment(ref _workerSequence)}";
        var workerLogger = _loggerFactory.CreateLogger<AgyWorker>();
        var newWorker = new AgyWorker(
            newId,
            model,
            effort,
            conversationId: null,
            _options,
            _transcriptReader,
            workerLogger);

        await newWorker.InitializeAsync(cancellationToken);

        lock (_lock)
        {
            _workers.Add(newWorker);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _sessionWorkerMap[sessionId] = newWorker.Id;
            }
        }

        _logger.LogInformation("Spawned new worker {WorkerId} (PID {Pid}) for model {Model}, effort {Effort}",
            newWorker.Id, newWorker.ProcessId, model, effort);

        return newWorker;
    }

    public void ReleaseWorker(IAgyWorker worker)
    {
        lock (_lock)
        {
            if (worker.TurnCount >= _options.Value.MaxTurnsPerWorker || !worker.IsHealthy)
            {
                _logger.LogInformation("Retiring worker {WorkerId} (turns: {Turns}, healthy: {Healthy})",
                    worker.Id, worker.TurnCount, worker.IsHealthy);
                _workers.Remove(worker);
                UnbindWorkerSessions(worker.Id);
                _ = worker.TerminateAsync();
            }
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
            _sessionWorkerMap.Clear();
        }

        foreach (var worker in toRecycle)
        {
            try
            {
                await worker.TerminateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while terminating worker {WorkerId}", worker.Id);
            }
        }

        // Re-warm startup workers
        var warmCount = Math.Max(0, _options.Value.WarmWorkersOnStartup);
        for (int i = 0; i < warmCount; i++)
        {
            try
            {
                var newId = $"worker-{Interlocked.Increment(ref _workerSequence)}";
                var worker = new AgyWorker(
                    newId,
                    _options.Value.DefaultModel,
                    _options.Value.DefaultReasoningEffort,
                    conversationId: null,
                    _options,
                    _transcriptReader,
                    _loggerFactory.CreateLogger<AgyWorker>());

                await worker.InitializeAsync(cancellationToken);
                lock (_lock)
                {
                    _workers.Add(worker);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-warm standby worker during pool recycle");
            }
        }

        _logger.LogInformation("Worker pool recycle completed. Active warm workers: {Count}", _workers.Count);
    }

    public AgyPoolStats GetStats()
    {
        lock (_lock)
        {
            var infos = _workers.Select(w => new AgyWorkerInfo(
                w.Id,
                w.ProcessId,
                w.Model,
                w.Effort,
                w.CurrentConversationId,
                w.TurnCount,
                w.IsBusy,
                w.IsHealthy,
                w.WorkingSetBytes,
                w.CreatedUtc,
                w.LastActiveUtc
            )).ToList();

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
    }

    private void UnbindWorkerSessions(string workerId)
    {
        var keysToRemove = _sessionWorkerMap
            .Where(kvp => kvp.Value == workerId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _sessionWorkerMap.TryRemove(key, out _);
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _maintenanceTimer.WaitForNextTickAsync(ct);

                var idleTimeout = TimeSpan.FromMinutes(_options.Value.WorkerIdleTimeoutMinutes);
                var now = DateTime.UtcNow;
                var toRetire = new List<IAgyWorker>();

                lock (_lock)
                {
                    foreach (var worker in _workers.ToList())
                    {
                        if (!worker.IsHealthy)
                        {
                            _logger.LogWarning("Maintenance: pruning dead worker {WorkerId}", worker.Id);
                            _workers.Remove(worker);
                            UnbindWorkerSessions(worker.Id);
                            toRetire.Add(worker);
                        }
                        else if (!worker.IsBusy && (now - worker.LastActiveUtc) > idleTimeout)
                        {
                            // Keep at least WarmWorkersOnStartup alive
                            if (_workers.Count > _options.Value.WarmWorkersOnStartup)
                            {
                                _logger.LogInformation("Maintenance: retiring idle worker {WorkerId} (idle for {Mins} mins)",
                                    worker.Id, (now - worker.LastActiveUtc).TotalMinutes);
                                _workers.Remove(worker);
                                UnbindWorkerSessions(worker.Id);
                                toRetire.Add(worker);
                            }
                        }
                    }
                }

                foreach (var worker in toRetire)
                {
                    try
                    {
                        await worker.TerminateAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error terminating retired worker {WorkerId}", worker.Id);
                    }
                }

                // Check if we need to warm up standby workers
                var neededWarm = _options.Value.WarmWorkersOnStartup - _workers.Count;
                if (neededWarm > 0 && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var newId = $"worker-{Interlocked.Increment(ref _workerSequence)}";
                        var worker = new AgyWorker(
                            newId,
                            _options.Value.DefaultModel,
                            _options.Value.DefaultReasoningEffort,
                            conversationId: null,
                            _options,
                            _transcriptReader,
                            _loggerFactory.CreateLogger<AgyWorker>());

                        await worker.InitializeAsync(ct);
                        lock (_lock)
                        {
                            _workers.Add(worker);
                        }
                        _logger.LogInformation("Maintenance: warmed up standby worker {WorkerId} (PID {Pid})",
                            worker.Id, worker.ProcessId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to spawn standby worker during maintenance");
                    }
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
        _disposed = true;

        _cts.Cancel();
        _maintenanceTimer.Dispose();

        try
        {
            await _maintenanceTask;
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();

        List<IAgyWorker> toDispose;
        lock (_lock)
        {
            toDispose = new List<IAgyWorker>(_workers);
            _workers.Clear();
            _sessionWorkerMap.Clear();
        }

        foreach (var worker in toDispose)
        {
            try
            {
                await worker.TerminateAsync();
            }
            catch
            {
                // ignored
            }
        }

        GC.SuppressFinalize(this);
    }
}
