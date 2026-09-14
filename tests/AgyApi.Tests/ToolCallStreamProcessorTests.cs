using System.Text.Json;
using AgyApi.Features.ChatCompletions;
using Xunit;

namespace AgyApi.Tests;

public class ToolCallStreamProcessorTests
{
    [Fact]
    public void Process_DetectsAndParsesSingleToolCall()
    {
        var processor = new ToolCallStreamProcessor();
        var raw = "<tool_call>\n{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Tokyo\"}}\n</tool_call>";

        var (contentDelta, newCalls) = processor.Process(raw);
        var flushed = processor.Flush();

        Assert.True(processor.HasToolCalls);
        Assert.Single(processor.ToolCalls);
        var call = processor.ToolCalls[0];
        Assert.Equal("get_weather", call.Function.Name);
        Assert.Contains("Tokyo", call.Function.Arguments);
    }

    [Fact]
    public void Process_StreamsTextOutsideToolCalls()
    {
        var processor = new ToolCallStreamProcessor();
        var part1 = "Here is the weather: ";
        var part2 = "<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}</tool_call>";
        var part3 = " Hope that helps!";

        var r1 = processor.Process(part1);
        var r2 = processor.Process(part2);
        var r3 = processor.Process(part3);
        var flushed = processor.Flush();

        Assert.Equal("Here is the weather: ", r1.ContentDelta);
        Assert.NotNull(r2.NewToolCalls);
        Assert.Single(r2.NewToolCalls);
        Assert.Equal(" Hope that helps!", r3.ContentDelta);
        Assert.Contains("Here is the weather: ", processor.GetFullContent());
        Assert.Contains(" Hope that helps!", processor.GetFullContent());
    }

    [Fact]
    public void ExtractToolCalls_StaticMethodFindsMultipleCalls()
    {
        var text = @"I will check two places.
<tool_call>
{""name"": ""get_weather"", ""arguments"": {""city"": ""New York""}}
</tool_call>
<tool_call>
{""name"": ""get_weather"", ""arguments"": {""city"": ""London""}}
</tool_call>";

        var calls = ToolCallStreamProcessor.ExtractToolCalls(text);
        Assert.Equal(2, calls.Count);
        Assert.Equal("get_weather", calls[0].Function.Name);
        Assert.Contains("New York", calls[0].Function.Arguments);
        Assert.Equal("get_weather", calls[1].Function.Name);
        Assert.Contains("London", calls[1].Function.Arguments);

        var cleaned = ToolCallStreamProcessor.RemoveToolCallTags(text);
        Assert.Equal("I will check two places.", cleaned);
    }
}
