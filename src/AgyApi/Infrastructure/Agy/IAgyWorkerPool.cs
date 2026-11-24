namespace AgyApi.Infrastructure.Agy;

public record AgyWorkerInfo(
    string Id,
    int ProcessId,
    string Model,
    string Effort,
    string? ConversationId,
    int TurnCount,
    bool IsBusy,
    bool IsHealthy,
    long WorkingSetBytes,
    DateTime CreatedUtc,
    DateTime LastActiveUtc
);

public record AgyPoolStats(
    int TotalWorkers,
    int ActiveWorkers,
    int IdleWorkers,
    int TotalTurnsCompleted,
    long TotalWorkingSetBytes,
    IReadOnlyList<AgyWorkerInfo> Workers
);

public interface IAgyWorkerPool : IAsyncDisposable
{
    Task<IAgyWorker> AcquireWorkerAsync(
        string? sessionId,
        string model,
        string effort,
        string? conversationId = null,
        CancellationToken cancellationToken = default,
        bool enableAgentic = false);

    void ReleaseWorker(IAgyWorker worker);

    Task WarmUpAsync(CancellationToken cancellationToken = default);

    Task RecycleAllAsync(CancellationToken cancellationToken = default);

    AgyPoolStats GetStats();
}
