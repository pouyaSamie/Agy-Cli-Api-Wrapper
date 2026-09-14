using System.Text.Json.Serialization;

namespace AgyApi.Infrastructure.Agy;

public class AgyRawEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("step_update")]
    public AgyStepUpdate? StepUpdate { get; set; }

    [JsonPropertyName("result")]
    public AgyResult? Result { get; set; }
}

public class AgyStepUpdate
{
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("step_index")]
    public int StepIndex { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("step_type")]
    public string? StepType { get; set; }

    [JsonPropertyName("text_delta")]
    public string? TextDelta { get; set; }

    [JsonPropertyName("usage")]
    public AgyUsage? Usage { get; set; }
}

public class AgyResult
{
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("usage")]
    public AgyUsage? Usage { get; set; }
}

public class AgyUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("thinking_tokens")]
    public int ThinkingTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
