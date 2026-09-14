using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using AgyApi.Features.Models;
using AgyApi.Features.Sessions;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Background;

public class AgyBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AgyBackgroundService> _logger;
    private readonly AgyOptions _options;

    public AgyBackgroundService(
        IServiceProvider services,
        IOptions<AgyOptions> options,
        ILogger<AgyBackgroundService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AGY Background Hosted Service is starting...");

        // Warm-up phase
        try
        {
            using var scope = _services.CreateScope();
            var modelRegistry = scope.ServiceProvider.GetRequiredService<IModelRegistryService>();
            var apiKeyManager = scope.ServiceProvider.GetRequiredService<IApiKeyManager>();

            await apiKeyManager.InitializeAsync();
            await modelRegistry.InitializeAsync(stoppingToken);

            var models = modelRegistry.GetDiscoveredModels();
            _logger.LogInformation("AGY Background Hosted Service warm-up complete. Discovered {Count} models.", models.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AGY Background Hosted Service warm-up.");
        }

        // Maintenance loop (runs periodically while application is active)
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _services.CreateScope();
                var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();

                // Cleanup sessions inactive for > 12 hours
                var cleaned = sessionManager.CleanupExpiredSessions(TimeSpan.FromHours(12));
                if (cleaned > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} expired conversation sessions.", cleaned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error occurred during background maintenance cycle.");
            }
        }
    }
}
