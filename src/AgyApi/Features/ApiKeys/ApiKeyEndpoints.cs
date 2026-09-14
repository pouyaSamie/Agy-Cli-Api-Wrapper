namespace AgyApi.Features.ApiKeys;

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeySlice(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/keys").WithTags("ApiKeys");

        group.MapGet("", async (IApiKeyManager apiKeyManager) =>
        {
            var keys = await apiKeyManager.GetKeysAsync();
            return Results.Ok(keys);
        })
        .WithName("GetApiKeys");

        group.MapPost("", async (CreateKeyRequest req, IApiKeyManager apiKeyManager) =>
        {
            var keyItem = await apiKeyManager.CreateKeyAsync(req.Name ?? "API Key");
            return Results.Ok(new
            {
                id = keyItem.Id,
                name = keyItem.Name,
                key = keyItem.Key,
                masked_key = keyItem.MaskedKey,
                created_at = keyItem.CreatedAtUtc,
                prompt_tokens = keyItem.PromptTokens,
                completion_tokens = keyItem.CompletionTokens,
                total_tokens = keyItem.TotalTokens,
                request_count = keyItem.RequestCount
            });
        })
        .WithName("CreateApiKey");

        group.MapDelete("/{id}", async (string id, IApiKeyManager apiKeyManager) =>
        {
            var success = await apiKeyManager.RevokeKeyAsync(id);
            return success ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RevokeApiKey");

        group.MapPost("/{id}/reset", async (string id, IApiKeyManager apiKeyManager) =>
        {
            var success = await apiKeyManager.ResetUsageAsync(id);
            return success ? Results.Ok(new { message = "Usage reset to 0" }) : Results.NotFound();
        })
        .WithName("ResetApiKeyUsage");

        return app;
    }
}
