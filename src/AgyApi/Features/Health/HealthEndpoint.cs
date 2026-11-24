using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using AgyApi.Infrastructure.Agy;
using Microsoft.Extensions.Options;

namespace AgyApi.Features.Health;

public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealthSlice(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", async (
            IApiKeyManager apiKeyManager,
            IAgyWorkerPool workerPool,
            IOptions<AgyOptions> options,
            CancellationToken ct) =>
        {
            var sqliteHealthy = await apiKeyManager.IsHealthyAsync(ct);
            var agyBinaryFound = AgyProcessHelpers.IsExecutableAvailable(options.Value);
            var poolStats = workerPool.GetStats();

            var isHealthy = sqliteHealthy && agyBinaryFound;
            var payload = new
            {
                status = isHealthy ? "healthy" : "degraded",
                timestamp = DateTime.UtcNow,
                server = "AgyApi .NET OpenAI Wrapper",
                checks = new
                {
                    sqlite = sqliteHealthy ? "ok" : "error",
                    agy_cli = agyBinaryFound ? "ok" : "missing",
                    worker_pool = new
                    {
                        enabled = options.Value.EnableWorkerPool,
                        total_workers = poolStats.TotalWorkers,
                        active_workers = poolStats.ActiveWorkers,
                        idle_workers = poolStats.IdleWorkers
                    }
                }
            };

            return isHealthy
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("HealthCheck")
        .WithTags("System");

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "alive",
            timestamp = DateTime.UtcNow,
            server = "AgyApi .NET OpenAI Wrapper"
        }))
        .WithName("LivenessCheck")
        .WithTags("System");

        return app;
    }
}
