namespace AgyApi.Features.ApiKeys;

public interface IApiKeyAuthenticator
{
    bool HasAnyKeys();
    ApiKeyItem? AuthenticateKey(string? providedKey);
}
