using System.Text;
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
        string? sessionKey = null;
        if (context.Request.Headers.TryGetValue("X-Conversation-Id", out var convIdHeader) &&
            !string.IsNullOrWhiteSpace(convIdHeader))
        {
            sessionKey = convIdHeader.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue("X-Session-Id", out var sessIdHeader) &&
                 !string.IsNullOrWhiteSpace(sessIdHeader))
        {
            sessionKey = sessIdHeader.ToString().Trim();
        }
        else if (!string.IsNullOrWhiteSpace(request.User))
        {
            sessionKey = request.User.Trim();
        }
        else if (request.Messages != null && request.Messages.Count > 0)
        {
            var firstUserMsg = request.Messages.FirstOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
            if (firstUserMsg != null && !string.IsNullOrWhiteSpace(firstUserMsg.Content))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(firstUserMsg.Content.Trim()));
                sessionKey = "sess-" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
            }
        }

        var existingConvId = sessionKey != null ? _sessionManager.GetConversationId(sessionKey) : null;
        var isContinuing = !string.IsNullOrWhiteSpace(existingConvId);

        // Resolve model & agentic execution mode
        var modelResolution = _modelRegistry.ResolveModel(request.Model, request.ReasoningEffort);
        if (!modelResolution.IsValid)
        {
            return Results.NotFound(new
            {
                error = new
                {
                    message = modelResolution.ErrorMessage ?? "Model not found",
                    type = "invalid_request_error",
                    param = "model",
                    code = "model_not_found"
                }
            });
        }

        var enableAgentic = modelResolution.EnableAgenticMode;
        if (context.Request.Headers.TryGetValue("X-Agy-Mode", out var agyModeHeader) &&
            string.Equals(agyModeHeader.ToString().Trim(), "agent", StringComparison.OrdinalIgnoreCase))
        {
            enableAgentic = true;
        }

        // Determine if thinking mode should be activated
        var isThinkingModel = modelResolution.ActualModel.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("-high", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ||
                              modelResolution.ActualModel.Contains("claude", StringComparison.OrdinalIgnoreCase);

        var requestedThinking = !string.IsNullOrEmpty(request.ReasoningEffort) || _options.IncludeReasoningContent;
        var enableThinking = (isThinkingModel || requestedThinking) && _options.EnableThinkingDirectives;

        var prompt = _sessionManager.PreparePrompt(request, isContinuing, enableThinking, enableAgentic);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Results.BadRequest(new
            {
                error = new
                {
                    message = "Messages array cannot be empty.",
                    type = "invalid_request_error",
                    code = "empty_messages"
                }
            });
        }

        var executionRequest = new AgyExecutionRequest
        {
            Prompt = prompt,
            Model = modelResolution.ActualModel,
            ConversationId = existingConvId ?? sessionKey,
            EnableAgenticMode = enableAgentic,
            ReasoningEffort = request.ReasoningEffort ?? _options.DefaultReasoningEffort
        };

        var createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completionId = "chatcmpl-" + (existingConvId ?? Guid.NewGuid().ToString("N"));

        // Only emit tags in content if explicitly configured as "content" (standard OpenAI/LM Studio clients want clean content and reasoning in fields)
        var emitTagsInContent = string.Equals(_options.ReasoningFormat, "content", StringComparison.OrdinalIgnoreCase);

        if (request.Stream)
        {
            return await HandleStreamAsync(context, executionRequest, request, sessionKey, completionId, createdTimestamp, emitTagsInContent, prompt, cancellationToken);
        }
        else
        {
            return await HandleNonStreamAsync(context, executionRequest, request, sessionKey, completionId, createdTimestamp, emitTagsInContent, prompt, cancellationToken);
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
        string prompt,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var responseStream = context.Response.Body;
        var writer = new StreamWriter(responseStream, leaveOpen: true);
        var writeLock = new SemaphoreSlim(1, 1);

        async Task WriteRawChunkAsync(string payload)
        {
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await writer.WriteAsync(payload);
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                writeLock.Release();
            }
        }

        string? recordedConvId = executionRequest.ConversationId;
        bool hasSentRole = false;
        bool hasStreamedReasoning = false;
        AgyUsage? finalUsage = null;
        string? finalFullResponse = null;
        string? finalThinking = null;
        var streamProcessor = new ThoughtStreamProcessor(emitTagsInContent);
        var toolCallProcessor = new ToolCallStreamProcessor();

        // 1. Immediately emit role: assistant chunk so client receives immediate TTFT and active stream indication
        var initialRoleChunk = new ChatCompletionChunk
        {
            Id = completionId,
            Created = createdTimestamp,
            Model = request.Model,
            Choices =
            [
                new ChunkChoice
                {
                    Index = 0,
                    Delta = new ChunkDelta { Role = "assistant" }
                }
            ]
        };
        await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(initialRoleChunk)}\n\n");
        hasSentRole = true;

        // 2. Start background keep-alive ping loop to prevent client watchdogs (e.g. Hermes 60s idle timeout)
        var keepAliveSeconds = _options.StreamKeepAliveSeconds > 0 ? _options.StreamKeepAliveSeconds : 15;
        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAliveTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(keepAliveSeconds));
                while (await timer.WaitForNextTickAsync(keepAliveCts.Token))
                {
                    var pingChunk = new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = createdTimestamp,
                        Model = request.Model,
                        Choices =
                        [
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new ChunkDelta()
                            }
                        ]
                    };
                    await WriteRawChunkAsync($": keep-alive\n\ndata: {JsonSerializer.Serialize(pingChunk)}\n\n");
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination when stream concludes
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keep-alive ping loop ended");
            }
        }, cancellationToken);

        try
        {
            await foreach (var item in _agyProcess.ExecutePromptStreamAsync(executionRequest, cancellationToken))
            {
                if (!string.IsNullOrEmpty(item.ConversationId))
                {
                    recordedConvId = item.ConversationId;
                    completionId = "chatcmpl-" + recordedConvId;
                    if (!string.IsNullOrEmpty(sessionKey))
                    {
                        _sessionManager.SetConversationId(sessionKey, recordedConvId);
                    }
                }

                if (item.Usage != null)
                {
                    finalUsage = item.Usage;
                }

                if (!string.IsNullOrEmpty(item.FullResponse))
                {
                    finalFullResponse = item.FullResponse;
                }

                if (!string.IsNullOrEmpty(item.Thinking))
                {
                    finalThinking = item.Thinking;
                }

                if (!hasSentRole)
                {
                    var roleChunk = new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = createdTimestamp,
                        Model = request.Model,
                        Choices =
                        [
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new ChunkDelta { Role = "assistant" }
                            }
                        ]
                    };

                    await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(roleChunk)}\n\n");
                    hasSentRole = true;
                }

                // If item has explicit ReasoningDelta (from live stream)
                if (!string.IsNullOrEmpty(item.ReasoningDelta))
                {
                    hasStreamedReasoning = true;
                    var reasoningChunk = new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = createdTimestamp,
                        Model = request.Model,
                        Choices =
                        [
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new ChunkDelta
                                {
                                    ReasoningContent = item.ReasoningDelta,
                                    Reasoning = item.ReasoningDelta
                                }
                            }
                        ]
                    };
                    await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(reasoningChunk)}\n\n");
                }

            if (!string.IsNullOrEmpty(item.TextDelta))
            {
                var processed = streamProcessor.Process(item.TextDelta);

                // If reasoning was detected in this chunk (<think> tag stream), emit reasoning_content delta
                if (!string.IsNullOrEmpty(processed.ReasoningDelta))
                {
                    hasStreamedReasoning = true;
                    var reasoningChunk = new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = createdTimestamp,
                        Model = request.Model,
                        Choices =
                        [
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new ChunkDelta
                                {
                                    ReasoningContent = processed.ReasoningDelta,
                                    Reasoning = processed.ReasoningDelta
                                }
                            }
                        ]
                    };

                    await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(reasoningChunk)}\n\n");
                }

                // If content is ready to be emitted (answer text or tool calls)
                if (!string.IsNullOrEmpty(processed.ContentDelta))
                {
                    // If no reasoning has been streamed yet (model used native thinking without <think> tags in text),
                    // extract native thinking from transcript file BEFORE emitting the first content chunk!
                    if (!hasStreamedReasoning && !streamProcessor.HasSeenThinking && !string.IsNullOrEmpty(recordedConvId))
                    {
                        var transcriptThinking = await _transcriptReader.ExtractThinkingAsync(recordedConvId, cancellationToken);
                        if (string.IsNullOrEmpty(transcriptThinking))
                        {
                            await Task.Delay(50, cancellationToken);
                            transcriptThinking = await _transcriptReader.ExtractThinkingAsync(recordedConvId, cancellationToken);
                        }

                        if (!string.IsNullOrEmpty(transcriptThinking))
                        {
                            hasStreamedReasoning = true;
                            finalThinking = transcriptThinking;
                            await StreamReasoningInChunksAsync(WriteRawChunkAsync, completionId, createdTimestamp, request.Model, transcriptThinking, cancellationToken);
                        }
                    }

                    var toolProcessed = toolCallProcessor.Process(processed.ContentDelta);

                    if (!string.IsNullOrEmpty(toolProcessed.ContentDelta))
                    {
                        var contentChunk = new ChatCompletionChunk
                        {
                            Id = completionId,
                            Created = createdTimestamp,
                            Model = request.Model,
                            Choices =
                            [
                                new ChunkChoice
                                {
                                    Index = 0,
                                    Delta = new ChunkDelta { Content = toolProcessed.ContentDelta }
                                }
                            ]
                        };

                        await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(contentChunk)}\n\n");
                    }

                    if (toolProcessed.NewToolCalls != null && toolProcessed.NewToolCalls.Count > 0)
                    {
                        var tcChunk = new ChatCompletionChunk
                        {
                            Id = completionId,
                            Created = createdTimestamp,
                            Model = request.Model,
                            Choices =
                            [
                                new ChunkChoice
                                {
                                    Index = 0,
                                    Delta = new ChunkDelta
                                    {
                                        ToolCalls = toolProcessed.NewToolCalls
                                    }
                                }
                            ]
                        };

                        await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(tcChunk)}\n\n");
                    }
                }
            }

            if (!string.IsNullOrEmpty(item.Error))
            {
                _logger.LogError("Streaming error from AGY: {Error}", item.Error);
                var errChunk = new ChatCompletionChunk
                {
                    Id = completionId,
                    Created = createdTimestamp,
                    Model = request.Model,
                    Choices =
                    [
                        new ChunkChoice
                        {
                            Index = 0,
                            Delta = new ChunkDelta { Content = $"\n[Error: {item.Error}]\n" }
                        }
                    ]
                };
                await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(errChunk)}\n\n");
            }
        }

        // Flush any buffered tokens from thought stream processor
        var flushed = streamProcessor.Flush();
        if (!string.IsNullOrEmpty(flushed.ReasoningDelta))
        {
            hasStreamedReasoning = true;
            var reasoningChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = request.Model,
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta
                        {
                            ReasoningContent = flushed.ReasoningDelta,
                            Reasoning = flushed.ReasoningDelta
                        }
                    }
                ]
            };
                    await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(reasoningChunk)}\n\n");
        }
        if (!string.IsNullOrEmpty(flushed.ContentDelta))
        {
            var toolProcessedFlushed = toolCallProcessor.Process(flushed.ContentDelta);
            if (!string.IsNullOrEmpty(toolProcessedFlushed.ContentDelta))
            {
                var contentChunk = new ChatCompletionChunk
                {
                    Id = completionId,
                    Created = createdTimestamp,
                    Model = request.Model,
                    Choices =
                    [
                        new ChunkChoice
                        {
                            Index = 0,
                            Delta = new ChunkDelta { Content = toolProcessedFlushed.ContentDelta }
                        }
                    ]
                };
                await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(contentChunk)}\n\n");
            }

            if (toolProcessedFlushed.NewToolCalls != null && toolProcessedFlushed.NewToolCalls.Count > 0)
            {
                var tcChunk = new ChatCompletionChunk
                {
                    Id = completionId,
                    Created = createdTimestamp,
                    Model = request.Model,
                    Choices =
                    [
                        new ChunkChoice
                        {
                            Index = 0,
                            Delta = new ChunkDelta { ToolCalls = toolProcessedFlushed.NewToolCalls }
                        }
                    ]
                };
                await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(tcChunk)}\n\n");
            }
        }

        var toolFlushed = toolCallProcessor.Flush();
        if (!string.IsNullOrEmpty(toolFlushed.ContentDelta))
        {
            var contentChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = request.Model,
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta { Content = toolFlushed.ContentDelta }
                    }
                ]
            };
            await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(contentChunk)}\n\n");
        }
        if (toolFlushed.NewToolCalls != null && toolFlushed.NewToolCalls.Count > 0)
        {
            var tcChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = request.Model,
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta { ToolCalls = toolFlushed.NewToolCalls }
                    }
                ]
            };
            await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(tcChunk)}\n\n");
        }

        var fullContent = toolCallProcessor.GetFullContent();
        var fullThinking = streamProcessor.GetFullThinking();
        bool hasToolCalls = toolCallProcessor.HasToolCalls;

        // Safety fallback: if no reasoning was streamed, but reasoning was captured in finalThinking, stream it now
        if (!hasStreamedReasoning && string.IsNullOrEmpty(fullThinking) && !string.IsNullOrEmpty(finalThinking))
        {
            hasStreamedReasoning = true;
            await StreamReasoningInChunksAsync(WriteRawChunkAsync, completionId, createdTimestamp, request.Model, finalThinking, cancellationToken);
            fullThinking = finalThinking;
        }

        // If no content or tools were streamed, but we have a final response
        if (string.IsNullOrEmpty(fullContent) && !hasToolCalls && !string.IsNullOrEmpty(finalFullResponse))
        {
            var extraTools = ToolCallStreamProcessor.ExtractToolCalls(finalFullResponse);
            if (extraTools.Count > 0)
            {
                hasToolCalls = true;
                var tcChunk = new ChatCompletionChunk
                {
                    Id = completionId,
                    Created = createdTimestamp,
                    Model = request.Model,
                    Choices =
                    [
                        new ChunkChoice
                        {
                            Index = 0,
                            Delta = new ChunkDelta { ToolCalls = extraTools }
                        }
                    ]
                };
                await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(tcChunk)}\n\n");
            }
            else
            {
                var contentChunk = new ChatCompletionChunk
                {
                    Id = completionId,
                    Created = createdTimestamp,
                    Model = request.Model,
                    Choices =
                    [
                        new ChunkChoice
                        {
                            Index = 0,
                            Delta = new ChunkDelta { Content = finalFullResponse }
                        }
                    ]
                };
                await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(contentChunk)}\n\n");
                fullContent = finalFullResponse;
            }
        }

        // Fallback: check if transcript recorded a response that wasn't streamed
        if (string.IsNullOrWhiteSpace(fullContent) && !hasToolCalls && !string.IsNullOrEmpty(recordedConvId))
        {
            var transcriptContent = await _transcriptReader.ExtractLatestResponseAsync(recordedConvId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(transcriptContent))
            {
                var extraTools = ToolCallStreamProcessor.ExtractToolCalls(transcriptContent);
                if (extraTools.Count > 0)
                {
                    hasToolCalls = true;
                    var tcChunk = new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = createdTimestamp,
                        Model = request.Model,
                        Choices =
                        [
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new ChunkDelta { ToolCalls = extraTools }
                            }
                        ]
                    };
                    await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(tcChunk)}\n\n");
                }
                else
                {
                    var contentChunk = new ChatCompletionChunk
                    {
                        Id = completionId,
                        Created = createdTimestamp,
                        Model = request.Model,
                        Choices =
                        [
                            new ChunkChoice
                            {
                                Index = 0,
                                Delta = new ChunkDelta { Content = transcriptContent }
                            }
                        ]
                    };
                    await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(contentChunk)}\n\n");
                    fullContent = transcriptContent;
                }
            }
        }

        // Absolute fail-safe: Ensure client (e.g. Hermes) NEVER receives an empty message that breaks the conversation loop (only if no tools called)
        if (string.IsNullOrWhiteSpace(fullContent) && !hasToolCalls)
        {
            var fallbackText = !string.IsNullOrWhiteSpace(fullThinking)
                ? $"Based on my reasoning:\n\n{fullThinking}"
                : "I processed your request, but was unable to produce a complete response. Please try again.";

            var fallbackChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = request.Model,
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta { Content = fallbackText }
                    }
                ]
            };
            await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(fallbackChunk)}\n\n");
            fullContent = fallbackText;
        }

        // Send finish chunk
        var finishChunk = new ChatCompletionChunk
        {
            Id = completionId,
            Created = createdTimestamp,
            Model = request.Model,
            Choices =
            [
                new ChunkChoice
                {
                    Index = 0,
                    Delta = new ChunkDelta(),
                    FinishReason = hasToolCalls ? "tool_calls" : "stop"
                }
            ]
        };
        await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(finishChunk)}\n\n");

        var inTokens = (finalUsage != null && finalUsage.InputTokens > 0) ? finalUsage.InputTokens : Math.Max(1, prompt.Length / 4);
        var outTokens = (finalUsage != null && finalUsage.OutputTokens > 0) ? finalUsage.OutputTokens : Math.Max(1, (fullContent.Length + fullThinking.Length) / 4);
        var totTokens = (finalUsage != null && finalUsage.TotalTokens > 0) ? finalUsage.TotalTokens : (inTokens + outTokens);

        // Optional usage chunk for OpenAI streaming options
        if (request.StreamOptions?.IncludeUsage == true)
        {
            var usageChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = request.Model,
                Choices = [],
                Usage = new UsageStatistics
                {
                    PromptTokens = inTokens,
                    CompletionTokens = outTokens,
                    TotalTokens = totTokens
                }
            };
            await WriteRawChunkAsync($"data: {JsonSerializer.Serialize(usageChunk)}\n\n");
        }

        await WriteRawChunkAsync("data: [DONE]\n\n");

        // Record usage against authenticated API key
        if (context.Items.TryGetValue("AuthApiKeyId", out var kIdObj) && kIdObj is string authKeyId)
        {
            await _usageRecorder.RecordUsageAsync(authKeyId, inTokens, outTokens, totTokens);
        }

        return Results.Empty;
        }
        finally
        {
            keepAliveCts.Cancel();
            try
            {
                await keepAliveTask;
            }
            catch
            {
                // ignore cancellation
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
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await _agyProcess.ExecutePromptAsync(executionRequest, cancellationToken);

        if (!string.IsNullOrEmpty(result.ConversationId) && !string.IsNullOrEmpty(sessionKey))
        {
            _sessionManager.SetConversationId(sessionKey, result.ConversationId);
            completionId = "chatcmpl-" + result.ConversationId;
            context.Response.Headers.Append("X-Conversation-Id", result.ConversationId);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            return Results.Json(new
            {
                error = new
                {
                    message = result.Error,
                    type = "server_error",
                    code = "agy_error"
                }
            }, statusCode: StatusCodes.Status500InternalServerError);
        }

        // Process thinking and content
        var rawResponse = result.FullResponse ?? string.Empty;
        var processor = new ThoughtStreamProcessor(emitTagsInContent);
        processor.Process(rawResponse);

        var thinking = processor.GetFullThinking();
        if (string.IsNullOrEmpty(thinking) && !string.IsNullOrEmpty(result.Thinking))
        {
            thinking = result.Thinking;
        }

        string finalContent;
        if (emitTagsInContent)
        {
            // If thinking was only found via transcript fallback and not in rawResponse, prepend <think> tag
            if (!string.IsNullOrEmpty(thinking) && !rawResponse.Contains("<think>") && !rawResponse.Contains("<thought>"))
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

        if (string.IsNullOrWhiteSpace(finalContent) && !string.IsNullOrWhiteSpace(result.FullResponse))
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

        if (string.IsNullOrWhiteSpace(finalContent) && !hasToolCalls)
        {
            finalContent = !string.IsNullOrWhiteSpace(thinking)
                ? $"Based on my reasoning:\n\n{thinking}"
                : "I processed your request, but was unable to produce a complete response. Please try again.";
        }

        var inTokens = (result.Usage != null && result.Usage.InputTokens > 0) ? result.Usage.InputTokens : Math.Max(1, prompt.Length / 4);
        var outTokens = (result.Usage != null && result.Usage.OutputTokens > 0) ? result.Usage.OutputTokens : Math.Max(1, ((finalContent?.Length ?? 0) + (thinking?.Length ?? 0)) / 4);
        var totTokens = (result.Usage != null && result.Usage.TotalTokens > 0) ? result.Usage.TotalTokens : (inTokens + outTokens);

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
                        ReasoningContent = !string.IsNullOrEmpty(thinking) ? thinking : null,
                        Reasoning = !string.IsNullOrEmpty(thinking) ? thinking : null,
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

    private static async Task StreamReasoningInChunksAsync(
        Func<string, Task> writeRawChunk,
        string completionId,
        long createdTimestamp,
        string model,
        string thinking,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return;

        var chunks = SplitIntoThinkingChunks(thinking, 80);
        var delayMs = Math.Clamp(250 / Math.Max(1, chunks.Count), 2, 8);

        foreach (var chunkText in chunks)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var reasoningChunk = new ChatCompletionChunk
            {
                Id = completionId,
                Created = createdTimestamp,
                Model = model,
                Choices =
                [
                    new ChunkChoice
                    {
                        Index = 0,
                        Delta = new ChunkDelta
                        {
                            ReasoningContent = chunkText,
                            Reasoning = chunkText
                        }
                    }
                ]
            };

            await writeRawChunk($"data: {JsonSerializer.Serialize(reasoningChunk)}\n\n");
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }
    }

    private static List<string> SplitIntoThinkingChunks(string text, int targetChunkSize = 25)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text))
            return chunks;

        int index = 0;
        while (index < text.Length)
        {
            int remaining = text.Length - index;
            if (remaining <= targetChunkSize)
            {
                chunks.Add(text.Substring(index));
                break;
            }

            int minSearch = (int)(targetChunkSize * 0.7);
            int maxSearch = Math.Min((int)(targetChunkSize * 1.3), remaining);
            int bestBreak = -1;

            for (int i = minSearch; i <= maxSearch; i++)
            {
                char c = text[index + i - 1];
                if (c == ' ' || c == '\n' || c == '.' || c == ',' || c == ';' || c == '!' || c == '?')
                {
                    bestBreak = i;
                    if (c == ' ' || c == '\n') break;
                }
            }

            int chunkSize = bestBreak > 0 ? bestBreak : targetChunkSize;
            chunks.Add(text.Substring(index, chunkSize));
            index += chunkSize;
        }

        return chunks;
    }
}
