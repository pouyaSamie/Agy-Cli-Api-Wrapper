using AgyApi.Configuration;
using AgyApi.Features.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgyApi.Tests;

public class ModelRegistryTests
{
    private readonly ModelRegistryService _registry;

    public ModelRegistryTests()
    {
        var options = Options.Create(new AgyOptions
        {
            DefaultModel = "gemini-3.8-flash-high"
        });
        _registry = new ModelRegistryService(options, NullLogger<ModelRegistryService>.Instance);
    }

    [Fact]
    public void ResolveModel_WithAgentPrefix_EnablesAgenticMode()
    {
        var resolution = _registry.ResolveModel("agent:hermes");

        Assert.True(resolution.IsValid);
        Assert.True(resolution.EnableAgenticMode);
        Assert.Equal("gpt-oss-120b-medium", resolution.ActualModel);
    }

    [Fact]
    public void ResolveModel_WithNullOrEmpty_ReturnsDefaultModel()
    {
        var resolution = _registry.ResolveModel(null);

        Assert.True(resolution.IsValid);
        Assert.False(resolution.EnableAgenticMode);
        Assert.Equal("gemini-3.8-flash-high", resolution.ActualModel);
    }
}
