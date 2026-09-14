namespace AgyApi.Infrastructure.Agy;

public interface IAgyTranscriptReader
{
    Task<string?> ExtractThinkingAsync(string conversationId, CancellationToken cancellationToken = default);
    Task<string?> ExtractLatestResponseAsync(string conversationId, CancellationToken cancellationToken = default);
}
