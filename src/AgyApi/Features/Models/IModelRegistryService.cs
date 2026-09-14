namespace AgyApi.Features.Models;

public class ModelResolutionResult
{
    public required string ActualModel { get; set; }
    public bool EnableAgenticMode { get; set; }
    public bool IsValid { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public interface IModelRegistryService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    ModelResolutionResult ResolveModel(string? requestedModel, string? reasoningEffort = null);
    Task<ModelListResponse> GetModelsAsync(CancellationToken cancellationToken = default);
    IReadOnlyCollection<string> GetDiscoveredModels();
}
