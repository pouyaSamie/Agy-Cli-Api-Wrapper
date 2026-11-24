using System.Text.Json;
using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using AgyApi.Features.Models;
using AgyApi.Features.Sessions;
using AgyApi.Infrastructure.Agy;
using Microsoft.Extensions.Options;

namespace AgyApi.Features.ChatCompletions;

public class ChatCompletionHandler : IChatCompletionHandler
{
    private readonly IModelRegistryService _modelRegistry;
    private readonly ISessionManager _sessionManager;
    private readonly IAgyProcessService _agyProcess;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly IApiKeyUsageRecorder _usageRecorder;
    private readonly AgyOptions _options;
    private readonly ILogger<ChatCompletionHandler> _logger;

    public ChatCompletionHandler(
        IModelRegistryService modelRegistry,
        ISessionManager sessionManager,
        IAgyProcessService agyProcess,
        IAgyTranscriptReader transcriptReader,
        IApiKeyUsageRecorder usageRecorder,
        IOptions<AgyOptions> options,
        ILogger<ChatCompletionHandler> logger)
    {
        _modelRegistry = modelRegistry;
        _sessionManager = sessionManager;
        _agyProcess = agyProcess;
        _transcriptReader = transcriptReader;
        _usageRecorder = usageRecorder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IResult> HandleAsync(HttpContext context, ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        // Only stick sessions on explicit client session headers or OpenAI 'user', scoped per authenticated API key
        string? rawSessionKey = null;
        if (context.Request.Headers.TryGetValue("X-Conversation-Id", out var convIdHeader) &&
            !string.IsNullOrWhiteSpace(convIdHeader))
        {
            rawSessionKey = convIdHeader.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue("X-Session-Id", out var sessIdHeader) &&
                 !string.IsNullOrWhiteSpace(sessIdHeader))
        {
            rawSessionKey = sessIdHeader.ToString().Trim();
        }
        else if (!string.IsNullOrWhiteSpace(request.User))
        {
            rawSessionKey = request.User.Trim();
        }

        var callerScope = context.Items.TryGetValue("AuthApiKeyId", out var kIdScope) && kIdScope is string scopeId && !string.IsNullOrWhiteSpace(scopeId)
            ? scopeId
            : "local";

        var sessionKey = !string.IsNullOrWhiteSpace(rawSessionKey)
            ? $"{callerScope}:{rawSessionKey}"
            : null;

        var existingConvId = sessionKey != null ? _sessionManager.GetConversationId(sessionKey) : null;
        var isContinuing = !string.IsNullOrWhiteSpace(existingConvId);

        // Resolve model & enforce agentic execution policy
        var modelResolution = _modelRegistry.ResolveModel(request.Model, request.ReasoningEffort);
        if (!modelResolution.IsValid)
        {
            if (modelResolution.IsForbidden)
            {
                return CreateErrorResult(
                    StatusCodes.Status403Forbidden,
                    modelResolution.ErrorMessage ?? "Agentic mode is disabled on this server.",
                    "permission_error",
                    "model",
                    "agentic_mode_disabled");
            }

            return CreateErrorResult(
                StatusCodes.Status404NotFound,
                modelResolution.ErrorMessage ?? "Model not found",
                "invalid_request_error",
                "model",
                "model_not_found");
        }

        var enableAgentic = modelResolution.EnableAgenticMode;
        if (context.Request.Headers.TryGetValue("X-Agy-Mode", out var agyModeHeader) &&
            string.Equals(agyModeHeader.ToString().Trim(), "agent", StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.AllowAgenticTools)
            {
                return CreateErrorResult(
                    StatusCodes.Status403Forbidden,
                    "Agentic tool execution is disabled by server policy (Agy:AllowAgenticTools=false).",
                    "permission_error",
                    "X-Agy-Mode",
                    "agentic_mode_disabled");
            }
            enableAgentic = true;
        }

        var isThinkingModel = modelResolution.ActualModel.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("-high", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("-xhigh", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("claude", StringComparison.OrdinalIgnoreCase);

        var requestedThinking = !string.IsNullOrEmpty(request.ReasoningEffort) || _options.IncludeReasoningContent;
        var enableThinking = (isThinkingModel || requestedThinking) && _options.EnableThinkingDirectives;

        var prompt = _sessionManager.PreparePrompt(request, isContinuing, enableThinking, enableAgentic);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return CreateErrorResult(
                StatusCodes.Status400BadRequest,
                "Messages array cannot be empty.",
                "invalid_request_error",
                "messages",
                "empty_messages");
        }

        var executionRequest = new AgyExecutionRequest
        {
            Prompt = prompt,
            Model = modelResolution.ActualModel,
            SessionId = sessionKey,
            ConversationId = existingConvId,
            EnableAgenticMode = enableAgentic,
            ReasoningEffort = request.ReasoningEffort ?? _options.DefaultReasoningEffort
        };

        var createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N");

        var reasoningFormat = (_options.ReasoningFormat ?? "field").Trim().ToLowerInvariant();
        var emitTagsInContent = reasoningFormat is "tags" or "content" or "both";
        var emitReasoningFields = reasoningFormat is "field" or "both" || !emitTagsInContent;

        try
        {
            if (request.Stream)
            {
                return await HandleStreamAsync(context, executionRequest, request, sessionKey, completionId, createdTimestamp, emitTagsInContent, emitReasoningFields, prompt, cancellationToken);
            }

            return await HandleNonStreamAsync(context, executionRequest, request, sessionKey, completionId, createdTimestamp, emitTagsInContent, emitReasoningFields, prompt, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Worker pool saturated while handling chat completion");
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.RetryAfter = "5";
                return CreateErrorResult(
                    StatusCodes.Status503ServiceUnavailable,
                    "All inference workers are currently busy. Please retry shortly.",
                    "server_error",
                    null,
                    "worker_pool_saturated");
            }
            return Results.Empty;
        }
    }

    private async Task<IResult> HandleStreamAsync(
        HttpContext context,
        AgyExecutionRequest executionRequest,
        ChatCompletionRequest request,
        string? sessionKey,
        string completionId,
        long createdTimestamp,
        bool emitTagsInContent,
        bool emitReasoningFields,
        string prompt,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await using var writer = new StreamWriter(context.Response.Body, leaveOpen: true);
        using var writeLock = new SemaphoreSlim(1, 1);

        async Task WriteRawAsync(string payload, CancellationToken ct)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await writer.WriteAsync(payload);
                await writer.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        Task WriteChunkAsync(ChunkDelta delta, string? finishReason = null, UsageStatistics? usage = null, bool emptyChoices = false)
        {
            var chunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = request.Model,
                Choices = emptyChoices
                    ? []
                    : [new ChunkChoice { Index = 0, Delta = delta, FinishReason = finishReason }],
                Usage = usage
            };
            return WriteRawAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", cancellationToken);
        }

        ChunkDelta CreateReasoningDelta(string text) => new()
        {
            ReasoningContent = text,
            Reasoning = _options.EmitLegacyReasoningField ? text : null
        };

        string? recordedConvId = executionRequest.ConversationId;
        bool hasStreamedReasoning = false;
        bool hadStreamError = false;
        AgyUsage? finalUsage = null;
        string? finalFullResponse = null;
        string? finalThinking = null;
        var streamProcessor = new ThoughtStreamProcessor(emitTagsInContent);
        var toolCallProcessor = new ToolCallStreamProcessor();

        // Emit initial role chunk with immutable completionId
        await WriteChunkAsync(new ChunkDelta { Role = "assistant" });

        // Comment-only SSE heartbeat prevents proxy/client idle timeouts without injecting fake JSON deltas
        var keepAliveSeconds = _options.StreamKeepAliveSeconds > 0 ? _options.StreamKeepAliveSeconds : 15;
        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAliveTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(keepAliveSeconds));
                while (await timer.WaitForNextTickAsync(keepAliveCts.Token))
                {
                    await WriteRawAsync(": keep-alive\n\n", keepAliveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keep-alive ping loop ended");
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in _agyProcess.ExecutePromptStreamAsync(executionRequest, cancellationToken))
            {
                if (!string.IsNullOrEmpty(item.ConversationId))
                {
                    recordedConvId = item.ConversationId;
                    if (!string.IsNullOrEmpty(sessionKey))
                    {
                        _sessionManager.SetConversationId(sessionKey, recordedConvId);
                    }
                }

                if (item.Usage != null) finalUsage = item.Usage;
                if (!string.IsNullOrEmpty(item.FullResponse)) finalFullResponse = item.FullResponse;
                if (!string.IsNullOrEmpty(item.Thinking)) finalThinking = item.Thinking;

                if (!string.IsNullOrEmpty(item.Error))
                {
                    hadStreamError = true;
                    _logger.LogError("Streaming error from AGY: {Error}", item.Error);
                    var errPayload = JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            message = item.Error,
                            type = "server_error",
                            code = "agy_error"
                        }
                    });
                    await WriteRawAsync($"event: error\ndata: {errPayload}\n\n", cancellationToken);
                    break;
                }

                if (!string.IsNullOrEmpty(item.TextDelta))
                {
                    var processed = streamProcessor.Process(item.TextDelta);

                    if (!string.IsNullOrEmpty(processed.ReasoningDelta))
                    {
                        hasStreamedReasoning = true;
                        if (emitReasoningFields)
                        {
                            await WriteChunkAsync(CreateReasoningDelta(processed.ReasoningDelta));
                        }
                    }

                    if (!string.IsNullOrEmpty(processed.ContentDelta))
                    {
                        if (_options.IncludeReasoningContent &&
                            !hasStreamedReasoning &&
                            !streamProcessor.HasSeenThinking &&
                            !string.IsNullOrEmpty(recordedConvId))
                        {
                            var transcriptThinking = await _transcriptReader.ExtractThinkingAsync(recordedConvId, cancellationToken);
                            if (!string.IsNullOrEmpty(transcriptThinking))
                            {
                                hasStreamedReasoning = true;
                                finalThinking = transcriptThinking;
                                await EmitThinkingImmediateAsync(WriteChunkAsync, CreateReasoningDelta, transcriptThinking, emitReasoningFields, emitTagsInContent);
                            }
                        }

                        var toolProcessed = toolCallProcessor.Process(processed.ContentDelta);
                        if (!string.IsNullOrEmpty(toolProcessed.ContentDelta))
                        {
                            await WriteChunkAsync(new ChunkDelta { Content = toolProcessed.ContentDelta });
                        }
                        if (toolProcessed.NewToolCalls is { Count: > 0 })
                        {
                            await WriteChunkAsync(new ChunkDelta { ToolCalls = toolProcessed.NewToolCalls });
                        }
                    }
                }
            }

            if (!hadStreamError)
            {
                var flushed = streamProcessor.Flush();
                if (!string.IsNullOrEmpty(flushed.ReasoningDelta))
                {
                    hasStreamedReasoning = true;
                    if (emitReasoningFields)
                    {
                        await WriteChunkAsync(CreateReasoningDelta(flushed.ReasoningDelta));
                    }
                }
                if (!string.IsNullOrEmpty(flushed.ContentDelta))
                {
                    var toolProcessedFlushed = toolCallProcessor.Process(flushed.ContentDelta);
                    if (!string.IsNullOrEmpty(toolProcessedFlushed.ContentDelta))
                    {
                        await WriteChunkAsync(new ChunkDelta { Content = toolProcessedFlushed.ContentDelta });
                    }
                    if (toolProcessedFlushed.NewToolCalls is { Count: > 0 })
                    {
                        await WriteChunkAsync(new ChunkDelta { ToolCalls = toolProcessedFlushed.NewToolCalls });
                    }
                }

                var toolFlushed = toolCallProcessor.Flush();
                if (!string.IsNullOrEmpty(toolFlushed.ContentDelta))
                {
                    await WriteChunkAsync(new ChunkDelta { Content = toolFlushed.ContentDelta });
                }
                if (toolFlushed.NewToolCalls is { Count: > 0 })
                {
                    await WriteChunkAsync(new ChunkDelta { ToolCalls = toolFlushed.NewToolCalls });
                }

                var fullContent = toolCallProcessor.GetFullContent();
                var fullThinking = streamProcessor.GetFullThinking();
                bool hasToolCalls = toolCallProcessor.HasToolCalls;

                if (_options.IncludeReasoningContent && !hasStreamedReasoning && string.IsNullOrEmpty(fullThinking) && !string.IsNullOrEmpty(finalThinking))
                {
                    hasStreamedReasoning = true;
                    await EmitThinkingImmediateAsync(WriteChunkAsync, CreateReasoningDelta, finalThinking, emitReasoningFields, emitTagsInContent);
                    fullThinking = finalThinking;
                }

                // Fallback to result.FullResponse if delta chunks were omitted
                if (string.IsNullOrEmpty(fullContent) && !hasToolCalls && !string.IsNullOrEmpty(finalFullResponse))
                {
                    var extraTools = ToolCallStreamProcessor.ExtractToolCalls(finalFullResponse);
                    if (extraTools.Count > 0)
                    {
                        hasToolCalls = true;
                        await WriteChunkAsync(new ChunkDelta { ToolCalls = extraTools });
                    }
                    else
                    {
                        await WriteChunkAsync(new ChunkDelta { Content = finalFullResponse });
                        fullContent = finalFullResponse;
                    }
                }

                // Fallback to transcript if neither deltas nor result.Response arrived
                if (string.IsNullOrWhiteSpace(fullContent) && !hasToolCalls && !string.IsNullOrEmpty(recordedConvId))
                {
                    var transcriptContent = await _transcriptReader.ExtractLatestResponseAsync(recordedConvId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(transcriptContent))
                    {
                        var extraTools = ToolCallStreamProcessor.ExtractToolCalls(transcriptContent);
                        if (extraTools.Count > 0)
                        {
                            hasToolCalls = true;
                            await WriteChunkAsync(new ChunkDelta { ToolCalls = extraTools });
                        }
                        else
                        {
                            await WriteChunkAsync(new ChunkDelta { Content = transcriptContent });
                            fullContent = transcriptContent;
                        }
                    }
                }

                await WriteChunkAsync(new ChunkDelta(), finishReason: hasToolCalls ? "tool_calls" : "stop");

                var (inTokens, outTokens, totTokens) = ComputeTokenCounts(finalUsage, prompt, fullContent, fullThinking);

                if (request.StreamOptions?.IncludeUsage == true)
                {
                    await WriteChunkAsync(
                        new ChunkDelta(),
                        usage: new UsageStatistics
                        {
                            PromptTokens = inTokens,
                            CompletionTokens = outTokens,
                            TotalTokens = totTokens
                        },
                        emptyChoices: true);
                }

                await WriteRawAsync("data: [DONE]\n\n", cancellationToken);
            }

            return Results.Empty;
        }
        finally
        {
            await keepAliveCts.CancelAsync();
            try
            {
                await keepAliveTask;
            }
            catch
            {
                // ignored
            }

            // Always record usage (even if the client disconnected mid-stream)
            if (context.Items.TryGetValue("AuthApiKeyId", out var kIdObj) && kIdObj is string authKeyId)
            {
                var recordedContent = !string.IsNullOrEmpty(toolCallProcessor.GetFullContent())
                    ? toolCallProcessor.GetFullContent()
                    : (finalFullResponse ?? string.Empty);
                var recordedThinking = !string.IsNullOrEmpty(streamProcessor.GetFullThinking())
                    ? streamProcessor.GetFullThinking()
                    : (finalThinking ?? string.Empty);

                var (inTokens, outTokens, totTokens) = ComputeTokenCounts(finalUsage, prompt, recordedContent, recordedThinking);
                await _usageRecorder.RecordUsageAsync(authKeyId, inTokens, outTokens, totTokens);
            }
        }
    }

    private async Task<IResult> HandleNonStreamAsync(
        HttpContext context,
        AgyExecutionRequest executionRequest,
        ChatCompletionRequest request,
        string? sessionKey,
        string completionId,
        long createdTimestamp,
        bool emitTagsInContent,
        bool emitReasoningFields,
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await _agyProcess.ExecutePromptAsync(executionRequest, cancellationToken);

        if (!string.IsNullOrEmpty(result.ConversationId))
        {
            if (!string.IsNullOrEmpty(sessionKey))
            {
                _sessionManager.SetConversationId(sessionKey, result.ConversationId);
            }
            context.Response.Headers.Append("X-Conversation-Id", result.ConversationId);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            return CreateErrorResult(
                StatusCodes.Status500InternalServerError,
                result.Error,
                "server_error",
                null,
                "agy_error");
        }

        var rawResponse = result.FullResponse ?? string.Empty;
        var processor = new ThoughtStreamProcessor(emitTagsInContent);
        processor.Process(rawResponse);
        processor.Flush();

        var thinking = processor.GetFullThinking();
        if (string.IsNullOrEmpty(thinking) && !string.IsNullOrEmpty(result.Thinking))
        {
            thinking = result.Thinking;
        }

        string finalContent;
        if (emitTagsInContent)
        {
            if (!string.IsNullOrEmpty(thinking) && !rawResponse.Contains("<think>", StringComparison.OrdinalIgnoreCase) && !rawResponse.Contains("<thought>", StringComparison.OrdinalIgnoreCase))
            {
                finalContent = $"<think>\n{thinking}\n</think>\n\n{rawResponse.TrimStart()}";
            }
            else
            {
                finalContent = rawResponse;
            }
        }
        else
        {
            finalContent = processor.GetFullContent();
        }

        if (string.IsNullOrWhiteSpace(finalContent) && !string.IsNullOrWhiteSpace(result.FullResponse) && !processor.HasSeenThinking)
        {
            finalContent = result.FullResponse;
        }

        if (string.IsNullOrWhiteSpace(finalContent) && !string.IsNullOrEmpty(result.ConversationId))
        {
            finalContent = await _transcriptReader.ExtractLatestResponseAsync(result.ConversationId, cancellationToken) ?? string.Empty;
        }

        var extractedTools = ToolCallStreamProcessor.ExtractToolCalls(rawResponse);
        if (extractedTools.Count == 0 && !string.IsNullOrEmpty(finalContent))
        {
            extractedTools = ToolCallStreamProcessor.ExtractToolCalls(finalContent);
        }

        bool hasToolCalls = extractedTools.Count > 0;
        if (hasToolCalls)
        {
            finalContent = ToolCallStreamProcessor.RemoveToolCallTags(finalContent);
        }

        var (inTokens, outTokens, totTokens) = ComputeTokenCounts(result.Usage, prompt, finalContent, thinking);

        var response = new ChatCompletionResponse
        {
            Id = completionId,
            Created = createdTimestamp,
            Model = request.Model,
            Choices =
            [
                new ChatChoice
                {
                    Index = 0,
                    Message = new ChatResponseMessage
                    {
                        Role = "assistant",
                        Content = !string.IsNullOrWhiteSpace(finalContent) ? finalContent : (hasToolCalls ? null : string.Empty),
                        ReasoningContent = emitReasoningFields && !string.IsNullOrEmpty(thinking) ? thinking : null,
                        Reasoning = _options.EmitLegacyReasoningField && emitReasoningFields && !string.IsNullOrEmpty(thinking) ? thinking : null,
                        ToolCalls = hasToolCalls ? extractedTools : null
                    },
                    FinishReason = hasToolCalls ? "tool_calls" : "stop"
                }
            ],
            Usage = new UsageStatistics
            {
                PromptTokens = inTokens,
                CompletionTokens = outTokens,
                TotalTokens = totTokens
            }
        };

        if (context.Items.TryGetValue("AuthApiKeyId", out var kIdObj) && kIdObj is string authKeyId)
        {
            await _usageRecorder.RecordUsageAsync(authKeyId, inTokens, outTokens, totTokens);
        }

        return Results.Ok(response);
    }

    private static async Task EmitThinkingImmediateAsync(
        Func<ChunkDelta, string?, UsageStatistics?, bool, Task> writeChunk,
        Func<string, ChunkDelta> createReasoningDelta,
        string thinking,
        bool emitReasoningFields,
        bool emitTagsInContent)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return;

        if (emitReasoningFields)
        {
            await writeChunk(createReasoningDelta(thinking), null, null, false);
        }
        if (emitTagsInContent && !emitReasoningFields)
        {
            await writeChunk(new ChunkDelta { Content = $"<think>\n{thinking}\n</think>\n\n" }, null, null, false);
        }
    }

    private static (int InputTokens, int OutputTokens, int TotalTokens) ComputeTokenCounts(
        AgyUsage? usage,
        string prompt,
        string? content,
        string? thinking)
    {
        var inTokens = usage is { InputTokens: > 0 }
            ? usage.InputTokens
            : Math.Max(1, prompt.Length / 4);

        int outTokens;
        if (usage != null && (usage.OutputTokens > 0 || usage.ThinkingTokens > 0))
        {
            outTokens = usage.OutputTokens + usage.ThinkingTokens;
        }
        else
        {
            outTokens = Math.Max(1, ((content?.Length ?? 0) + (thinking?.Length ?? 0)) / 4);
        }

        var totTokens = usage is { TotalTokens: > 0 }
            ? Math.Max(usage.TotalTokens, inTokens + outTokens)
            : inTokens + outTokens;

        return (inTokens, outTokens, totTokens);
    }

    private static IResult CreateErrorResult(int statusCode, string message, string type, string? param, string code)
    {
        return Results.Json(new
        {
            error = new
            {
                message,
                type,
                param,
                code
            }
        }, statusCode: statusCode);
    }
}
