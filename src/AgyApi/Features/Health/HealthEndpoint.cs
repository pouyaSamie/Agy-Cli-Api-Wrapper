namespace AgyApi.Features.Health;

public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealthSlice(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            server = "AgyApi .NET OpenAI Wrapper"
        }))
        .WithName("HealthCheck")
        .WithTags("System");

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            server = "AgyApi .NET OpenAI Wrapper"
        }))
        .WithName("HealthAlias")
        .WithTags("System");

        return app;
    }
}
