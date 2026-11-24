namespace AgyApi.Infrastructure.Agy;

public interface IAgyWorker : IAsyncDisposable
{
    string Id { get; }
    int ProcessId { get; }
    string? CurrentConversationId { get; }
    string Model { get; }
    string Effort { get; }
    bool EnableAgenticMode { get; }
    int TurnCount { get; }
    DateTime CreatedUtc { get; }
    DateTime LastActiveUtc { get; }
    long WorkingSetBytes { get; }
    bool IsHealthy { get; }
    bool IsBusy { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgyStreamItem> ExecuteTurnStreamAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    Task TerminateAsync();
}
