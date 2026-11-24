using System.Text;
using AgyApi.Infrastructure.Agy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgyApi.Tests;

public class AgyTranscriptReaderTests : IDisposable
{
    private readonly string _tempRoot;

    public AgyTranscriptReaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "agy-transcript-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData("--flag")]
    [InlineData("")]
    public async Task ExtractThinkingAsync_RejectsInvalidAndTraversalIds(string badId)
    {
        var reader = new AgyTranscriptReader(NullLogger<AgyTranscriptReader>.Instance, _tempRoot);
        var result = await reader.ExtractThinkingAsync(badId);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReverseScan_MultiChunkFile_ExtractsOnlyLatestTurnAndStopsAtUserBoundary()
    {
        var convId = "conv-1234-test";
        var logsDir = Path.Combine(_tempRoot, convId, ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);
        var transcriptPath = Path.Combine(logsDir, "transcript_full.jsonl");

        var sb = new StringBuilder();
        // Turn 1 with large padding (> 96 KB so reverse scanner crosses multiple 32KB chunks)
        sb.AppendLine("{\"type\":\"USER_INPUT\",\"content\":\"first question\"}");
        sb.AppendLine($"{{\"type\":\"PLANNER_RESPONSE\",\"thinking\":\"OLD_TURN_THINKING\",\"content\":\"{new string('A', 90_000)}\"}}");
        // Turn 2 with no thinking (only USER_INPUT + response without thinking)
        sb.AppendLine("{\"type\":\"USER_INPUT\",\"content\":\"second question\"}");
        sb.AppendLine("{\"type\":\"PLANNER_RESPONSE\",\"content\":\"LATEST_ANSWER\"}");

        await File.WriteAllTextAsync(transcriptPath, sb.ToString(), Encoding.UTF8);

        var reader = new AgyTranscriptReader(NullLogger<AgyTranscriptReader>.Instance, _tempRoot);

        // Must NOT leak OLD_TURN_THINKING across the USER_INPUT boundary
        var thinking = await reader.ExtractThinkingAsync(convId);
        Assert.Null(thinking);

        // Must extract LATEST_ANSWER from the tail of the file
        var latestContent = await reader.ExtractLatestResponseAsync(convId);
        Assert.Equal("LATEST_ANSWER", latestContent);
    }

    [Fact]
    public async Task ReverseScan_ExtractsLatestTurnThinking()
    {
        var convId = "conv-5678-test";
        var logsDir = Path.Combine(_tempRoot, convId, ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);
        var transcriptPath = Path.Combine(logsDir, "transcript_full.jsonl");

        var content = string.Join("\n", [
            "{\"type\":\"USER_INPUT\",\"content\":\"turn 1\"}",
            "{\"type\":\"PLANNER_RESPONSE\",\"thinking\":\"turn 1 thought\",\"content\":\"turn 1 answer\"}",
            "{\"type\":\"USER_INPUT\",\"content\":\"turn 2\"}",
            "{\"type\":\"PLANNER_RESPONSE\",\"thinking\":\"turn 2 thought — café 🚀\",\"content\":\"turn 2 answer\"}"
        ]);
        await File.WriteAllTextAsync(transcriptPath, content, Encoding.UTF8);

        var reader = new AgyTranscriptReader(NullLogger<AgyTranscriptReader>.Instance, _tempRoot);
        var thinking = await reader.ExtractThinkingAsync(convId);

        Assert.Equal("turn 2 thought — café 🚀", thinking);
    }
}
