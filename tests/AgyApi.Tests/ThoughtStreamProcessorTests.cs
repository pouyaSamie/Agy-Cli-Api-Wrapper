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
    public void Process_WithDefaultEmitTagsFalse_StripsTagsFromContent()
    {
        var processor = new ThoughtStreamProcessor(emitTagsInContent: false);

        var c1 = processor.Process("<think>internal thoughts</think>Final answer");
        var flushed = processor.Flush();

        Assert.Equal("internal thoughts", c1.ReasoningDelta);
        Assert.Equal("Final answer", c1.ContentDelta);
        Assert.Null(flushed.ContentDelta);
        Assert.Equal("internal thoughts", processor.GetFullThinking());
        Assert.Equal("Final answer", processor.GetFullContent());
    }

    [Fact]
    public void Process_SingleCharAngleBracketChunks_BuffersPartialTagsCorrectly()
    {
        var processor = new ThoughtStreamProcessor(emitTagsInContent: false);

        var chunks = new[] { "<", "think", ">", "step 1", "<", "/", "think", ">", "Result!" };
        var content = "";
        var reasoning = "";

        foreach (var ch in chunks)
        {
            var res = processor.Process(ch);
            if (res.ContentDelta != null) content += res.ContentDelta;
            if (res.ReasoningDelta != null) reasoning += res.ReasoningDelta;
        }
        var flushed = processor.Flush();
        if (flushed.ContentDelta != null) content += flushed.ContentDelta;
        if (flushed.ReasoningDelta != null) reasoning += flushed.ReasoningDelta;

        Assert.Equal("step 1", reasoning);
        Assert.Equal("Result!", content);
        Assert.Equal("step 1", processor.GetFullThinking());
        Assert.Equal("Result!", processor.GetFullContent());
    }

    [Fact]
    public void Process_TrailingPartialAngleBracket_FlushesOnFlush()
    {
        var processor = new ThoughtStreamProcessor(emitTagsInContent: false);

        var c1 = processor.Process("a <");
        Assert.Equal("a ", c1.ContentDelta);

        var flushed = processor.Flush();
        Assert.Equal("<", flushed.ContentDelta);
        Assert.Equal("a <", processor.GetFullContent());
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
