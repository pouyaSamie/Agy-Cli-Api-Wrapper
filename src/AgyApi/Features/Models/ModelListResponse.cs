using System.Text.Json.Serialization;

namespace AgyApi.Features.Models;

public class ModelListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<ModelData> Data { get; set; } = [];
}

public class ModelData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "google";
}
