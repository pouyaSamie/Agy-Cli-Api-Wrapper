namespace AgyApi.Features.Models;

public static class ModelsEndpoint
{
    public static IEndpointRouteBuilder MapModelSlice(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", async (IModelRegistryService modelRegistry, CancellationToken ct) =>
        {
            var response = await modelRegistry.GetModelsAsync(ct);
            return Results.Ok(response);
        })
        .WithName("ListModels")
        .WithTags("Models");

        return app;
    }
}
