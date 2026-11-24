using AgyApi.Configuration;
using AgyApi.Features.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgyApi.Tests;

public class ModelRegistryTests
{
    private static readonly string[] SampleDiscoveredModels =
    [
        "gemini-4-neon-low",
        "gemini-4-neon-medium",
        "gemini-4-neon-high",
        "gemini-4-neon-xhigh",
        "gemini-4-hydrogen-high",
        "gemini-3.8-flash-low",
        "gemini-3.8-flash-medium",
        "gemini-3.8-flash-high",
        "claude-opus-5-5-high",
        "claude-sonnet-5-5-high",
        "gpt-oss-120b-medium"
    ];

    private readonly ModelRegistryService _registry;
    private readonly ModelRegistryService _initializedRegistry;
    private readonly ModelRegistryService _agenticRegistry;

    public ModelRegistryTests()
    {
        var defaultOptions = Options.Create(new AgyOptions
        {
            DefaultModel = "gemini-3.8-flash-high",
            AllowAgenticTools = false
        });
        var agenticOptions = Options.Create(new AgyOptions
        {
            DefaultModel = "gemini-3.8-flash-high",
            AllowAgenticTools = true
        });
        _registry = new ModelRegistryService(defaultOptions, NullLogger<ModelRegistryService>.Instance);
        _initializedRegistry = ModelRegistryService.CreateWithPreloadedModels(defaultOptions, NullLogger<ModelRegistryService>.Instance, SampleDiscoveredModels);
        _agenticRegistry = ModelRegistryService.CreateWithPreloadedModels(agenticOptions, NullLogger<ModelRegistryService>.Instance, SampleDiscoveredModels);
    }

    [Fact]
    public void ResolveModel_WithAgentPrefix_WhenAgenticAllowed_EnablesAgenticMode()
    {
        var resolution = _agenticRegistry.ResolveModel("agent:hermes");

        Assert.True(resolution.IsValid);
        Assert.False(resolution.IsForbidden);
        Assert.True(resolution.EnableAgenticMode);
        Assert.Equal("gpt-oss-120b-medium", resolution.ActualModel);
    }

    [Theory]
    [InlineData("agent:hermes")]
    [InlineData("gemini-3.8-flash-high:agent")]
    public void ResolveModel_WithAgentModifier_WhenAgenticDisabled_ReturnsForbidden(string requestedModel)
    {
        var resolution = _initializedRegistry.ResolveModel(requestedModel);

        Assert.False(resolution.IsValid);
        Assert.True(resolution.IsForbidden);
        Assert.False(resolution.EnableAgenticMode);
        Assert.NotNull(resolution.ErrorMessage);
    }

    [Fact]
    public void ResolveModel_WithNullOrEmpty_ReturnsDefaultModel()
    {
        var resolution = _registry.ResolveModel(null);

        Assert.True(resolution.IsValid);
        Assert.False(resolution.EnableAgenticMode);
        Assert.Equal("gemini-3.8-flash-high", resolution.ActualModel);
    }

    [Theory]
    [InlineData("gpt-4o", "gemini-4-hydrogen-high")]
    [InlineData("gpt-4", "gemini-4-hydrogen-high")]
    [InlineData("gpt-4o-mini", "gemini-3.8-flash-high")]
    [InlineData("o1", "claude-opus-5-5-high")]
    [InlineData("o3-mini", "claude-sonnet-5-5-high")]
    [InlineData("claude-3-5-sonnet", "claude-sonnet-5-5-high")]
    [InlineData("claude-3-opus", "claude-opus-5-5-high")]
    [InlineData("hermes", "gpt-oss-120b-medium")]
    public void ResolveModel_Aliases_MapToValidAgy15Models(string requested, string expected)
    {
        var resolution = _initializedRegistry.ResolveModel(requested);

        Assert.True(resolution.IsValid);
        Assert.Equal(expected, resolution.ActualModel);
    }

    [Fact]
    public void ResolveModel_EffortSwitch_HandlesXHighAndLow()
    {
        var toLow = _initializedRegistry.ResolveModel("gemini-4-neon-xhigh", "low");
        Assert.True(toLow.IsValid);
        Assert.Equal("gemini-4-neon-low", toLow.ActualModel);

        var toXHigh = _initializedRegistry.ResolveModel("gemini-4-neon-medium", "xhigh");
        Assert.True(toXHigh.IsValid);
        Assert.Equal("gemini-4-neon-xhigh", toXHigh.ActualModel);
    }

    [Fact]
    public void ResolveModel_UnknownModelWhenInitialized_ReturnsInvalid()
    {
        var resolution = _initializedRegistry.ResolveModel("nonexistent-model-xyz");

        Assert.False(resolution.IsValid);
        Assert.False(resolution.IsForbidden);
        Assert.NotNull(resolution.ErrorMessage);
    }

    [Fact]
    public async Task GetModelsAsync_WhenAgenticDisabled_DoesNotAdvertiseAgentModels()
    {
        var response = await _initializedRegistry.GetModelsAsync();
        Assert.DoesNotContain(response.Data, m => m.Id.StartsWith("agent:", StringComparison.OrdinalIgnoreCase));
    }
}
