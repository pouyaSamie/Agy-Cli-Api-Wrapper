namespace AgyApi.Features.ApiKeys;

public interface IApiKeyUsageRecorder
{
    Task RecordUsageAsync(string keyId, long promptTokens, long completionTokens, long totalTokens);
}
