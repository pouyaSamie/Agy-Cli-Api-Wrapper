using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using AgyApi.Features.ChatCompletions;
using AgyApi.Features.Models;
using AgyApi.Features.Sessions;
using AgyApi.Infrastructure.Agy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgyApi.Tests;

public class ChatCompletionHandlerTests
{
    private sealed class FakeUsageRecorder : IApiKeyUsageRecorder
    {
        public List<(string KeyId, long Prompt, long Completion, long Total)> Records { get; } = [];

        public Task RecordUsageAsync(string keyId, long promptTokens, long completionTokens, long totalTokens)
        {
            Records.Add((keyId, promptTokens, completionTokens, totalTokens));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTranscriptReader : IAgyTranscriptReader
    {
        public Task<string?> ExtractThinkingAsync(string conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<string?> ExtractLatestResponseAsync(string conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeProcessService : IAgyProcessService
    {
        public List<AgyExecutionRequest> CapturedRequests { get; } = [];
        public Func<AgyExecutionRequest, CancellationToken, IAsyncEnumerable<AgyStreamItem>>? StreamHandler { get; set; }

        public async IAsyncEnumerable<AgyStreamItem> ExecutePromptStreamAsync(
            AgyExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request);
            if (StreamHandler != null)
            {
                await foreach (var item in StreamHandler(request, cancellationToken))
                {
                    yield return item;
                }
                yield break;
            }

            yield return new AgyStreamItem { ConversationId = "conv-1", TextDelta = "Hello " };
            yield return new AgyStreamItem
            {
                ConversationId = "conv-1",
                TextDelta = "world",
                IsComplete = true,
                FullResponse = "Hello world",
                Usage = new AgyUsage { InputTokens = 10, OutputTokens = 5, ThinkingTokens = 7, TotalTokens = 22 }
            };
        }

        public async Task<AgyStreamItem> ExecutePromptAsync(AgyExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request);
            var sb = new StringBuilder();
            AgyStreamItem? last = null;
            await foreach (var item in ExecutePromptStreamAsync(request, cancellationToken))
            {
                if (!string.IsNullOrEmpty(item.TextDelta)) sb.Append(item.TextDelta);
                last = item;
            }
            return last ?? new AgyStreamItem { IsComplete = true, FullResponse = sb.ToString() };
        }
    }

    private static ChatCompletionHandler CreateHandler(
        FakeProcessService processService,
        FakeUsageRecorder usageRecorder,
        AgyOptions? customOptions = null)
    {
        var opts = Options.Create(customOptions ?? new AgyOptions
        {
            AllowAgenticTools = false,
            DefaultModel = "gemini-3.8-flash-medium",
            EmitLegacyReasoningField = false
        });

        var registry = ModelRegistryService.CreateWithPreloadedModels(
            opts,
            NullLogger<ModelRegistryService>.Instance,
            ["gemini-3.8-flash-medium", "gemini-3.8-flash-high"]);

        return new ChatCompletionHandler(
            registry,
            new SessionManager(),
            processService,
            new NullTranscriptReader(),
            usageRecorder,
            opts,
            NullLogger<ChatCompletionHandler>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
    }

    [Fact]
    public async Task HandleAsync_WithXAgyModeAgentHeader_WhenDisabled_Returns403()
    {
        var proc = new FakeProcessService();
        var usage = new FakeUsageRecorder();
        var handler = CreateHandler(proc, usage);

        var ctx = CreateHttpContext();
        ctx.Request.Headers["X-Agy-Mode"] = "agent";

        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Messages = [new ChatMessage { Role = "user", Content = "run ls" }]
        };

        var result = await handler.HandleAsync(ctx, req, CancellationToken.None);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(proc.CapturedRequests);
    }

    [Fact]
    public async Task HandleStreamAsync_KeepsCompletionIdImmutable_AndRecordsThinkingTokens()
    {
        var proc = new FakeProcessService();
        var usage = new FakeUsageRecorder();
        var handler = CreateHandler(proc, usage);

        var ctx = new DefaultHttpContext();
        ctx.Items["AuthApiKeyId"] = "key-abc";
        ctx.Response.Body = new MemoryStream();

        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Stream = true,
            Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
        };

        await handler.HandleAsync(ctx, req, CancellationToken.None);

        ctx.Response.Body.Position = 0;
        var sseText = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var ids = sseText
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.GetProperty("id").GetString())
            .Distinct()
            .ToList();

        Assert.Single(ids); // Completion ID MUST NOT mutate mid-stream!
        Assert.Contains("data: [DONE]", sseText);

        Assert.Single(usage.Records);
        Assert.Equal("key-abc", usage.Records[0].KeyId);
        Assert.Equal(10, usage.Records[0].Prompt);
        Assert.Equal(12, usage.Records[0].Completion); // OutputTokens (5) + ThinkingTokens (7)
        Assert.Equal(22, usage.Records[0].Total);
    }

    [Fact]
    public async Task HandleStreamAsync_ClientDisconnectMidStream_StillRecordsUsage()
    {
        using var cts = new CancellationTokenSource();
        var proc = new FakeProcessService
        {
            StreamHandler = (r, ct) => CancelMidStreamAsync(cts, ct)
        };

        var usage = new FakeUsageRecorder();
        var handler = CreateHandler(proc, usage);

        var ctx = new DefaultHttpContext();
        ctx.Items["AuthApiKeyId"] = "key-cancel";
        ctx.Response.Body = new MemoryStream();

        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Stream = true,
            Messages = [new ChatMessage { Role = "user", Content = "Write a long essay" }]
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.HandleAsync(ctx, req, cts.Token));

        Assert.Single(usage.Records);
        Assert.Equal("key-cancel", usage.Records[0].KeyId);
        Assert.True(usage.Records[0].Total > 0);
    }

    private static async IAsyncEnumerable<AgyStreamItem> CancelMidStreamAsync(
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgyStreamItem { ConversationId = "conv-cancel", TextDelta = "partial output text" };
        cts.Cancel();
        await Task.Delay(100, ct);
    }

    [Fact]
    public async Task HandleAsync_IdenticalFirstMessagesWithoutSessionHeader_DoNotCollide()
    {
        var proc = new FakeProcessService();
        var usage = new FakeUsageRecorder();
        var handler = CreateHandler(proc, usage);

        var req1 = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
        };
        var req2 = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Messages = [new ChatMessage { Role = "user", Content = "Hello" }]
        };

        var ctx1 = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var ctx2 = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await handler.HandleAsync(ctx1, req1, CancellationToken.None);
        await handler.HandleAsync(ctx2, req2, CancellationToken.None);

        Assert.All(proc.CapturedRequests, r => Assert.Null(r.SessionId));
    }

    [Fact]
    public async Task HandleNonStreamAsync_FlushesTrailingAngleBracket()
    {
        var proc = new FakeProcessService
        {
            StreamHandler = (_, _) => YieldTrailingBracketAsync()
        };
        var usage = new FakeUsageRecorder();
        var handler = CreateHandler(proc, usage);

        var ctx = CreateHttpContext();

        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Stream = false,
            Messages = [new ChatMessage { Role = "user", Content = "math" }]
        };

        var result = await handler.HandleAsync(ctx, req, CancellationToken.None);
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Position = 0;
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Assert.Equal("1 <", content);
    }

    private static async IAsyncEnumerable<AgyStreamItem> YieldTrailingBracketAsync()
    {
        await Task.Yield();
        yield return new AgyStreamItem
        {
            ConversationId = "conv-math",
            IsComplete = true,
            FullResponse = "1 <"
        };
    }
}
