using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using AgyApi.Configuration;
using AgyApi.Infrastructure.Agy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgyApi.Features.Models;

public class ModelRegistryService : IModelRegistryService, IDisposable
{
    private static readonly FrozenDictionary<string, string> StaticAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = "gemini-4-hydrogen-high",
        ["gpt-4o-mini"] = "gemini-3.8-flash-high",
        ["gpt-4-turbo"] = "gemini-4-hydrogen-high",
        ["gpt-4"] = "gemini-4-hydrogen-high",
        ["gpt-3.5-turbo"] = "gemini-3.8-flash-low",
        ["o1"] = "claude-opus-5-5-high",
        ["o1-mini"] = "claude-sonnet-5-5-high",
        ["o3-mini"] = "claude-sonnet-5-5-high",
        ["claude-3-5-sonnet"] = "claude-sonnet-5-5-high",
        ["claude-3-opus"] = "claude-opus-5-5-high",
        ["hermes"] = "gpt-oss-120b-medium",
        ["hermes-3"] = "gpt-oss-120b-medium",
        ["nous-hermes"] = "gpt-oss-120b-medium"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] EffortSuffixes = ["-xhigh", "-high", "-medium", "-low", "-max"];
    private static readonly string[] AdvertisedAliases = ["gpt-4o", "gpt-4o-mini", "o1", "o3-mini", "claude-3-5-sonnet", "hermes-3"];
    private static readonly TimeSpan FailureRetryCooldown = TimeSpan.FromSeconds(10);

    private readonly AgyOptions _options;
    private readonly ILogger<ModelRegistryService> _logger;
    private readonly ConcurrentBag<string> _discoveredModels = [];
    private volatile bool _initialized;
    private DateTime _lastFailedAttemptUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    [ActivatorUtilitiesConstructor]
    public ModelRegistryService(IOptions<AgyOptions> options, ILogger<ModelRegistryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public static ModelRegistryService CreateWithPreloadedModels(
        IOptions<AgyOptions> options,
        ILogger<ModelRegistryService> logger,
        IEnumerable<string> preloadedModels)
    {
        var service = new ModelRegistryService(options, logger);
        foreach (var model in preloadedModels)
        {
            if (!string.IsNullOrWhiteSpace(model) && !service._discoveredModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                service._discoveredModels.Add(model.Trim());
            }
        }
        service._initialized = true;
        return service;
    }

    public IReadOnlyCollection<string> GetDiscoveredModels() => _discoveredModels.ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        if (_lastFailedAttemptUtc != DateTime.MinValue && (DateTime.UtcNow - _lastFailedAttemptUtc) < FailureRetryCooldown)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (_lastFailedAttemptUtc != DateTime.MinValue && (DateTime.UtcNow - _lastFailedAttemptUtc) < FailureRetryCooldown)
            {
                return;
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = AgyProcessHelpers.ResolveExecutablePath(_options),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processStartInfo.ArgumentList.Add("models");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var process = new Process { StartInfo = processStartInfo };
            try
            {
                process.Start();

                // Read stdout and stderr concurrently to prevent OS pipe buffer deadlocks
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(timeoutCts.Token);

                var output = stdoutTask.Result;
                var error = stderrTask.Result;

                if (process.ExitCode == 0)
                {
                    var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Fetching", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                        {
                            var modelName = parts[0].Trim();
                            if (!_discoveredModels.Contains(modelName, StringComparer.OrdinalIgnoreCase))
                            {
                                _discoveredModels.Add(modelName);
                            }
                        }
                    }

                    _logger.LogInformation("Successfully discovered {Count} native AGY models", _discoveredModels.Count);
                    _initialized = true;
                }
                else
                {
                    _lastFailedAttemptUtc = DateTime.UtcNow;
                    _logger.LogWarning("Failed to query native models from agy (exit {Code}): {Error}", process.ExitCode, error.Trim());
                }
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastFailedAttemptUtc = DateTime.UtcNow;
            _logger.LogError(ex, "Exception querying native models from agy; will retry after cooldown");
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
            var requestedAgentic = false;

            if (model.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            {
                requestedAgentic = true;
                model = model["agent:".Length..].Trim();
            }
            else if (model.EndsWith(":agent", StringComparison.OrdinalIgnoreCase))
            {
                requestedAgentic = true;
                model = model[..^":agent".Length].Trim();
            }

            if (requestedAgentic)
            {
                if (!_options.AllowAgenticTools)
                {
                    return new ModelResolutionResult
                    {
                        ActualModel = string.Empty,
                        EnableAgenticMode = false,
                        IsValid = false,
                        IsForbidden = true,
                        ErrorMessage = "Agentic tool execution is disabled by server policy (Agy:AllowAgenticTools=false)."
                    };
                }
                enableAgentic = true;
            }

            if (_discoveredModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                targetModel = _discoveredModels.First(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                string? mappedModel = null;
                if (string.Equals(model, "default", StringComparison.OrdinalIgnoreCase))
                {
                    mappedModel = _options.DefaultModel;
                }
                else
                {
                    StaticAliasMap.TryGetValue(model, out mappedModel);
                }

                if (mappedModel != null)
                {
                    if (!_discoveredModels.IsEmpty && !_discoveredModels.Contains(mappedModel, StringComparer.OrdinalIgnoreCase))
                    {
                        var fallback = _discoveredModels.Contains(_options.DefaultModel, StringComparer.OrdinalIgnoreCase)
                            ? _options.DefaultModel
                            : _discoveredModels.First();
                        _logger.LogWarning("Alias '{Alias}' mapped to '{MappedModel}', which was not found in discovered models. Falling back to '{Fallback}'.",
                            model, mappedModel, fallback);
                        targetModel = fallback;
                    }
                    else
                    {
                        targetModel = mappedModel;
                    }
                }
                else
                {
                    var defaultEffort = !string.IsNullOrWhiteSpace(_options.DefaultReasoningEffort)
                        ? _options.DefaultReasoningEffort.ToLowerInvariant()
                        : "medium";

                    var candidate = $"{model}-{defaultEffort}";
                    if (_discoveredModels.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = _discoveredModels.First(m => string.Equals(m, candidate, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (_discoveredModels.Contains($"{model}-high", StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = _discoveredModels.First(m => string.Equals(m, $"{model}-high", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (_discoveredModels.Contains($"{model}-medium", StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = _discoveredModels.First(m => string.Equals(m, $"{model}-medium", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (!_discoveredModels.IsEmpty)
                    {
                        return new ModelResolutionResult
                        {
                            ActualModel = string.Empty,
                            EnableAgenticMode = enableAgentic,
                            IsValid = false,
                            ErrorMessage = $"The model '{requestedModel}' does not exist."
                        };
                    }
                    else
                    {
                        targetModel = _options.DefaultModel;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            var effort = reasoningEffort.Trim().ToLowerInvariant();
            foreach (var suffix in EffortSuffixes)
            {
                if (targetModel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var baseName = targetModel[..^suffix.Length];
                    var candidateVariant = $"{baseName}-{effort}";
                    if (_discoveredModels.Contains(candidateVariant, StringComparer.OrdinalIgnoreCase))
                    {
                        targetModel = _discoveredModels.First(m => string.Equals(m, candidateVariant, StringComparison.OrdinalIgnoreCase));
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in _discoveredModels)
        {
            if (seen.Add(m))
            {
                list.Data.Add(new ModelData { Id = m, Created = now, OwnedBy = "google" });
            }

            if (_options.AllowAgenticTools)
            {
                var agentId = $"agent:{m}";
                if (seen.Add(agentId))
                {
                    list.Data.Add(new ModelData { Id = agentId, Created = now, OwnedBy = "antigravity" });
                }
            }
        }

        foreach (var alias in AdvertisedAliases)
        {
            if (seen.Add(alias))
            {
                list.Data.Add(new ModelData { Id = alias, Created = now, OwnedBy = "openai-compat" });
            }
        }

        return list;
    }

    public void Dispose()
    {
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
