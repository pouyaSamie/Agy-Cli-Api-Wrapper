using System.Collections.Concurrent;
using System.Diagnostics;
using AgyApi.Configuration;
using Microsoft.Extensions.Options;

namespace AgyApi.Features.Models;

public class ModelRegistryService : IModelRegistryService
{
    private readonly AgyOptions _options;
    private readonly ILogger<ModelRegistryService> _logger;
    private readonly ConcurrentBag<string> _discoveredModels = [];
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public ModelRegistryService(IOptions<AgyOptions> options, ILogger<ModelRegistryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyCollection<string> GetDiscoveredModels() => _discoveredModels.ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                Arguments = "models",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("Fetching") || string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                    {
                        var modelName = parts[0].Trim();
                        if (!_discoveredModels.Contains(modelName))
                        {
                            _discoveredModels.Add(modelName);
                        }
                    }
                }

                _logger.LogInformation("Successfully discovered {Count} native AGY models", _discoveredModels.Count);
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                _logger.LogWarning("Failed to query native models from agy: {Error}", error);
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception querying native models from agy");
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public ModelResolutionResult ResolveModel(string? requestedModel)
    {
        return ResolveModel(requestedModel, null);
    }

    public ModelResolutionResult ResolveModel(string? requestedModel, string? reasoningEffort)
    {
        var enableAgentic = _options.AllowAgenticTools;

        string targetModel;
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            targetModel = _options.DefaultModel;
        }
        else
        {
            var model = requestedModel.Trim();

            // Check prefix / suffix modifiers
            if (model.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            {
                enableAgentic = true;
                model = model["agent:".Length..].Trim();
            }
            else if (model.EndsWith(":agent", StringComparison.OrdinalIgnoreCase))
            {
                enableAgentic = true;
                model = model[..^":agent".Length].Trim();
            }

            // Direct match against discovered models
            if (_discoveredModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                targetModel = _discoveredModels.First(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // Map common OpenAI aliases to best local equivalents
                var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-4o"] = "gemini-3.1-pro-high",
                    ["gpt-4o-mini"] = "gemini-3.8-flash-high",
                    ["gpt-4-turbo"] = "gemini-3.1-pro-high",
                    ["gpt-4"] = "gemini-3.1-pro-high",
                    ["gpt-3.5-turbo"] = "gemini-3.8-flash-low",
                    ["o1"] = "claude-opus-4-6-thinking",
                    ["o1-mini"] = "claude-sonnet-4-6",
                    ["o3-mini"] = "claude-sonnet-4-6",
                    ["claude-3-5-sonnet"] = "claude-sonnet-4-6",
                    ["claude-3-opus"] = "claude-opus-4-6-thinking",
                    ["hermes"] = "gpt-oss-120b-medium",
                    ["hermes-3"] = "gpt-oss-120b-medium",
                    ["nous-hermes"] = "gpt-oss-120b-medium",
                    ["default"] = _options.DefaultModel
                };

                if (aliasMap.TryGetValue(model, out var mappedModel))
                {
                    targetModel = mappedModel;
                }
                else
                {
                    var defaultEffort = !string.IsNullOrWhiteSpace(_options.DefaultReasoningEffort)
                        ? _options.DefaultReasoningEffort.ToLowerInvariant()
                        : "medium";

                    var candidate = $"{model}-{defaultEffort}";
                    if (_discoveredModels.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = candidate;
                    }
                    else if (_discoveredModels.Contains($"{model}-high", StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = $"{model}-high";
                    }
                    else
                    {
                        targetModel = _options.DefaultModel;
                    }
                }
            }
        }

        // If reasoningEffort is provided (low, medium, high), dynamically adapt targetModel to the matching effort variant
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            var effort = reasoningEffort.Trim().ToLowerInvariant();
            foreach (var suffix in new[] { "-high", "-medium", "-low" })
            {
                if (targetModel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var baseName = targetModel[..^suffix.Length];
                    var candidateVariant = $"{baseName}-{effort}";
                    if (_discoveredModels.Contains(candidateVariant, StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = candidateVariant;
                    }
                    break;
                }
            }
        }

        return new ModelResolutionResult
        {
            ActualModel = targetModel,
            EnableAgenticMode = enableAgentic
        };
    }

    public async Task<ModelListResponse> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var list = new ModelListResponse();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Add discovered models
        foreach (var m in _discoveredModels)
        {
            list.Data.Add(new ModelData { Id = m, Created = now, OwnedBy = "google" });
            list.Data.Add(new ModelData { Id = $"agent:{m}", Created = now, OwnedBy = "antigravity" });
        }

        // Add compatibility aliases
        var aliases = new[] { "gpt-4o", "gpt-4o-mini", "o1", "o3-mini", "claude-3-5-sonnet", "hermes-3" };
        foreach (var alias in aliases)
        {
            if (!list.Data.Any(d => d.Id == alias))
            {
                list.Data.Add(new ModelData { Id = alias, Created = now, OwnedBy = "openai-compat" });
            }
        }

        return list;
    }
}
