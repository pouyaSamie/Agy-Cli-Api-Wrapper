namespace AgyApi.Features.ApiKeys;

public interface IApiKeyManager : IApiKeyAuthenticator, IApiKeyUsageRecorder
{
    Task InitializeAsync();
    bool IsValidKey(string? providedKey);
    Task<ApiKeyItem> CreateKeyAsync(string name);
    Task<IReadOnlyList<ApiKeyItem>> GetKeysAsync();
    Task<bool> RevokeKeyAsync(string id);
    Task<bool> ResetUsageAsync(string id);
}
