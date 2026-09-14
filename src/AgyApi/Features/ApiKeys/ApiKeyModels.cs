using System.Text.Json.Serialization;

namespace AgyApi.Features.ApiKeys;

public class ApiKeyItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default Key";

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    private string? _maskedKey;

    [JsonPropertyName("masked_key")]
    public string MaskedKey
    {
        get => !string.IsNullOrEmpty(_maskedKey) ? _maskedKey : (Key.Length > 12 ? $"{Key[..10]}...{Key[^4..]}" : Key);
        set => _maskedKey = value;
    }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("last_used_at")]
    public DateTime? LastUsedAtUtc { get; set; }

    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("request_count")]
    public long RequestCount { get; set; }
}

public class CreateKeyRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
