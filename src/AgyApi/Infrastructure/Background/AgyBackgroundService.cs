using AgyApi.Features.ApiKeys;
using AgyApi.Features.Models;
using AgyApi.Features.Sessions;
using AgyApi.Infrastructure.Agy;

namespace AgyApi.Infrastructure.Background;

public class AgyBackgroundService : BackgroundService
{
    private readonly IModelRegistryService _modelRegistry;
    private readonly IApiKeyManager _apiKeyManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAgyWorkerPool _workerPool;
    private readonly ILogger<AgyBackgroundService> _logger;

    public AgyBackgroundService(
        IModelRegistryService modelRegistry,
        IApiKeyManager apiKeyManager,
        ISessionManager sessionManager,
        IAgyWorkerPool workerPool,
        ILogger<AgyBackgroundService> logger)
    {
        _modelRegistry = modelRegistry;
        _apiKeyManager = apiKeyManager;
        _sessionManager = sessionManager;
        _workerPool = workerPool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AGY Background Hosted Service is starting...");

        // Warm-up phase
        try
        {
            await _apiKeyManager.InitializeAsync();
            await _modelRegistry.InitializeAsync(stoppingToken);

            var models = _modelRegistry.GetDiscoveredModels();
            _logger.LogInformation("Model discovery warm-up complete. Discovered {Count} models.", models.Count);

            await _workerPool.WarmUpAsync(stoppingToken);
            _logger.LogInformation("Worker pool startup warm-up complete. Active workers: {Count}", _workerPool.GetStats().TotalWorkers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AGY Background Hosted Service warm-up.");
        }

        // Maintenance loop (runs periodically while application is active)
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                var cleaned = _sessionManager.CleanupExpiredSessions(TimeSpan.FromHours(12));
                if (cleaned > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} expired conversation sessions.", cleaned);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error occurred during background maintenance cycle.");
            }
        }
    }
}
