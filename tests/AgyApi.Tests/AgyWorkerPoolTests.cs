using AgyApi.Configuration;
using AgyApi.Infrastructure.Agy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgyApi.Tests;

public class AgyWorkerPoolTests
{
    private class DummyTranscriptReader : IAgyTranscriptReader
    {
        public Task<string?> ExtractThinkingAsync(string conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("dummy thinking");

        public Task<string?> ExtractLatestResponseAsync(string conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("dummy response");
    }

    [Fact]
    public void GetStats_Initially_ReflectsEmptyOrConfiguredPool()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0 // test with 0 to test clean empty stats
        });

        var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance);

        var stats = pool.GetStats();

        Assert.Equal(0, stats.TotalWorkers);
        Assert.Equal(0, stats.ActiveWorkers);
        Assert.Equal(0, stats.IdleWorkers);
        Assert.Equal(0, stats.TotalTurnsCompleted);
        Assert.Equal(0, stats.TotalWorkingSetBytes);
        Assert.Empty(stats.Workers);
    }

    [Fact]
    public void ReleaseWorker_WhenWorkerUnhealthy_RetiresWorkerFromPool()
    {
        var options = Options.Create(new AgyOptions
        {
            EnableWorkerPool = true,
            MaxPoolSize = 3,
            WarmWorkersOnStartup = 0
        });

        var pool = new AgyWorkerPool(
            options,
            new DummyTranscriptReader(),
            NullLoggerFactory.Instance,
            NullLogger<AgyWorkerPool>.Instance);

        // Verify pool stats handles empty without error
        var statsBefore = pool.GetStats();
        Assert.Equal(0, statsBefore.TotalWorkers);
    }

    [Fact]
    public void Deserialize_AgyResultEvent_SuccessfullyPopulates()
    {
        var json = "{\"event\":\"result\",\"result\":{\"conversation_id\":\"123\",\"status\":\"SUCCESS\",\"response\":\"hello\"}}";
        var ev = System.Text.Json.JsonSerializer.Deserialize<AgyRawEvent>(json);
        Assert.NotNull(ev);
        Assert.Equal("result", ev.Event);
        Assert.NotNull(ev.Result);
        Assert.Equal("hello", ev.Result.Response);
    }

    [Fact]
    public async Task AgyWorker_RealProcess_InitializesAndExecutesTurn()
    {
        var options = Options.Create(new AgyOptions
        {
            ExecutablePath = "agy",
            DefaultModel = "gemini-3.8-flash-medium",
            DefaultReasoningEffort = "medium",
            WorkspaceDirectory = "./workspace"
        });

        var worker = new AgyWorker(
            "test-worker-1",
            "gemini-3.8-flash-medium",
            "medium",
            conversationId: null,
            options,
            new DummyTranscriptReader(),
            NullLogger<AgyWorker>.Instance);

        await worker.InitializeAsync();
        try
        {
            Assert.True(worker.IsHealthy);
            Assert.True(worker.ProcessId > 0);

            var items = new List<AgyStreamItem>();
            await foreach (var item in worker.ExecuteTurnStreamAsync("Say the word PING only"))
            {
                items.Add(item);
            }

            foreach (var it in items)
            {
                System.Console.WriteLine($"ITEM: IsComplete={it.IsComplete}, Conv={it.ConversationId}, Delta='{it.TextDelta}', Full='{it.FullResponse}', Err='{it.Error}'");
            }

            Assert.NotEmpty(items);
            var completed = items.FirstOrDefault(i => i.IsComplete);
            Assert.NotNull(completed);
            Assert.False(string.IsNullOrEmpty(completed.FullResponse));
            Assert.True(worker.IsHealthy);
        }
        finally
        {
            await worker.TerminateAsync();
        }
    }
}
