namespace AgyApi.Features.ChatCompletions;

public record ProcessedChunk(
    string? ContentDelta,
    string? ReasoningDelta,
    bool IsThinking
);

public interface IThoughtStreamProcessor
{
    ProcessedChunk Process(string incomingDelta);
    ProcessedChunk Flush();
    string GetFullThinking();
    string GetFullContent();
    bool HasSeenThinking { get; }
    bool IsInsideThink { get; }
}
