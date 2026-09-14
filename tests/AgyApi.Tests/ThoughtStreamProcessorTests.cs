using AgyApi.Features.ChatCompletions;
using Xunit;

namespace AgyApi.Tests;

public class ThoughtStreamProcessorTests
{
    [Fact]
    public void Process_WithThinkTags_ExtractsReasoningContent_AndRetainsTagsInContent()
    {
        var processor = new ThoughtStreamProcessor(emitTagsInContent: true);

        var chunk1 = processor.Process("<think>\nPlanning ");
        Assert.NotNull(chunk1.ReasoningDelta);
        Assert.Contains("Planning", chunk1.ReasoningDelta);
        Assert.NotNull(chunk1.ContentDelta);
        Assert.Contains("<think>", chunk1.ContentDelta);

        var chunk2 = processor.Process("step 1\n</think>\nHere is the answer");
        Assert.NotNull(chunk2.ReasoningDelta);
        Assert.Contains("step 1", chunk2.ReasoningDelta);
        Assert.NotNull(chunk2.ContentDelta);
        Assert.Contains("</think>", chunk2.ContentDelta);
        Assert.Contains("Here is the answer", chunk2.ContentDelta);

        Assert.Equal("Planning step 1", processor.GetFullThinking().Trim());
        Assert.Contains("Here is the answer", processor.GetFullContent());
    }

    [Fact]
    public void Process_WithoutThinkTags_StreamsContentDirectly()
    {
        var processor = new ThoughtStreamProcessor(emitTagsInContent: true);

        var chunk = processor.Process("Hello world!");
        Assert.Null(chunk.ReasoningDelta);
        Assert.Equal("Hello world!", chunk.ContentDelta);
        Assert.Equal(string.Empty, processor.GetFullThinking());
        Assert.Equal("Hello world!", processor.GetFullContent());
    }
}
