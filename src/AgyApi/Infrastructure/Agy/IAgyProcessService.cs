namespace AgyApi.Infrastructure.Agy;

public class AgyStreamItem
{
    public string? ConversationId { get; set; }
    public string? TextDelta { get; set; }
    public string? ReasoningDelta { get; set; }
    public string? Thinking { get; set; }
    public bool IsComplete { get; set; }
    public string? FullResponse { get; set; }
    public AgyUsage? Usage { get; set; }
    public string? Error { get; set; }
}

public class AgyExecutionRequest
{
    public required string Prompt { get; set; }
    public required string Model { get; set; }
    public string? SessionId { get; set; }
    public string? ConversationId { get; set; }
    public bool EnableAgenticMode { get; set; }
    public string? ReasoningEffort { get; set; }
}

public interface IAgyProcessService
{
    IAsyncEnumerable<AgyStreamItem> ExecutePromptStreamAsync(
        AgyExecutionRequest request,
        CancellationToken cancellationToken = default);

    Task<AgyStreamItem> ExecutePromptAsync(
        AgyExecutionRequest request,
        CancellationToken cancellationToken = default);
}
