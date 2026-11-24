using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgyApi.Configuration;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Agy;

public class AgyProcessService : IAgyProcessService
{
    private const int MaxStderrChars = 8192;
    private readonly AgyOptions _options;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly IAgyWorkerPool _workerPool;
    private readonly ILogger<AgyProcessService> _logger;
    private readonly KeyedAsyncLock _conversationLocks = new();

    public AgyProcessService(
        IOptions<AgyOptions> options,
        IAgyTranscriptReader transcriptReader,
        IAgyWorkerPool workerPool,
        ILogger<AgyProcessService> logger)
    {
        _options = options.Value;
        _transcriptReader = transcriptReader;
        _workerPool = workerPool;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgyStreamItem> ExecutePromptStreamAsync(
        AgyExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lockKey = !string.IsNullOrWhiteSpace(request.SessionId)
            ? request.SessionId
            : request.ConversationId;

        using var conversationLease = string.IsNullOrWhiteSpace(lockKey)
            ? null
            : await _conversationLocks.AcquireAsync(lockKey, cancellationToken);

        var effectiveAgentic = request.EnableAgenticMode && _options.AllowAgenticTools;

        if (_options.EnableWorkerPool)
        {
            var effort = !string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? request.ReasoningEffort
                : _options.DefaultReasoningEffort;

            IAgyWorker? worker = null;
            string? acquireError = null;
            try
            {
                try
                {
                    worker = await _workerPool.AcquireWorkerAsync(
                        request.SessionId,
                        request.Model,
                        effort,
                        request.ConversationId,
                        cancellationToken,
                        effectiveAgentic);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to acquire AGY worker for model {Model}", request.Model);
                    acquireError = ex.Message;
                }

                if (acquireError != null)
                {
                    yield return new AgyStreamItem
                    {
                        ConversationId = request.ConversationId,
                        IsComplete = true,
                        Error = acquireError
                    };
                    yield break;
                }

                await foreach (var item in worker!.ExecuteTurnStreamAsync(request.Prompt, cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                if (worker != null)
                {
                    _workerPool.ReleaseWorker(worker);
                }
            }
            yield break;
        }

        Process? process = null;
        Task? writeInputTask = null;
        Task<string>? readStderrTask = null;

        try
        {
            var effort = !string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? request.ReasoningEffort
                : _options.DefaultReasoningEffort;

            var psi = AgyProcessHelpers.BuildCliProcessStartInfo(
                _options,
                request.Model,
                effort,
                request.ConversationId,
                effectiveAgentic,
                streamJsonInput: false);

            _logger.LogInformation("Spawning one-shot AGY process for model: {Model}, effort: {Effort}, agentic: {Agentic}, conversation: {ConvId}, promptLength: {PromptLen}",
                request.Model, effort, effectiveAgentic, request.ConversationId ?? "new", request.Prompt.Length);

            process = new Process { StartInfo = psi };
            process.Start();

            var localProcess = process;
            readStderrTask = ReadBoundedStderrAsync(localProcess.StandardError, cancellationToken);

            // Stream prompt into StandardInput and close to signal EOF
            writeInputTask = Task.Run(async () =>
            {
                try
                {
                    await localProcess.StandardInput.WriteAsync(request.Prompt.AsMemory(), cancellationToken);
                    await localProcess.StandardInput.FlushAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Normal on disconnect
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to write prompt to AGY StandardInput");
                }
                finally
                {
                    try
                    {
                        localProcess.StandardInput.Close();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }, CancellationToken.None);

            string? currentConversationId = request.ConversationId;
            var responseBuilder = new StringBuilder();
            AgyUsage? lastUsage = null;
            var hasReceivedResult = false;

            using var reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                AgyRawEvent? rawEvent = null;
                try
                {
                    rawEvent = JsonSerializer.Deserialize<AgyRawEvent>(line);
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Non-JSON output from AGY: {Line}", line);
                    continue;
                }

                if (rawEvent == null) continue;

                if (!string.IsNullOrEmpty(rawEvent.ConversationId))
                {
                    currentConversationId = rawEvent.ConversationId;
                }

                switch (rawEvent.Event)
                {
                    case "init":
                        yield return new AgyStreamItem
                        {
                            ConversationId = currentConversationId
                        };
                        break;

                    case "step_update":
                        var step = rawEvent.StepUpdate;
                        if (step != null)
                        {
                            if (!string.IsNullOrEmpty(step.ConversationId))
                            {
                                currentConversationId = step.ConversationId;
                            }

                            if (step.Usage != null)
                            {
                                lastUsage = step.Usage;
                            }

                            if (!string.IsNullOrEmpty(step.TextDelta))
                            {
                                responseBuilder.Append(step.TextDelta);
                                yield return new AgyStreamItem
                                {
                                    ConversationId = currentConversationId,
                                    TextDelta = step.TextDelta,
                                    Usage = step.Usage
                                };
                            }
                        }
                        break;

                    case "result":
                        var result = rawEvent.Result;
                        if (result != null)
                        {
                            hasReceivedResult = true;
                            if (!string.IsNullOrEmpty(result.ConversationId))
                            {
                                currentConversationId = result.ConversationId;
                            }
                            if (result.Usage != null)
                            {
                                lastUsage = result.Usage;
                            }

                            string? transcriptThinking = null;
                            if (_options.IncludeReasoningContent && !string.IsNullOrEmpty(currentConversationId))
                            {
                                transcriptThinking = await _transcriptReader.ExtractThinkingAsync(currentConversationId, cancellationToken);
                            }

                            yield return new AgyStreamItem
                            {
                                ConversationId = currentConversationId,
                                IsComplete = true,
                                FullResponse = result.Response ?? responseBuilder.ToString(),
                                Thinking = transcriptThinking,
                                Usage = lastUsage,
                                Error = result.Error
                            };
                        }
                        break;
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            var err = readStderrTask != null ? await readStderrTask : string.Empty;

            if (process.ExitCode != 0)
            {
                _logger.LogError("AGY exited with code {Code}: {Err}", process.ExitCode, err);
                yield return new AgyStreamItem
                {
                    ConversationId = currentConversationId,
                    IsComplete = true,
                    Error = string.IsNullOrWhiteSpace(err) ? $"Process exited with code {process.ExitCode}" : err
                };
            }
            else if (!hasReceivedResult)
            {
                string? transcriptThinking = null;
                string? transcriptContent = null;
                if (!string.IsNullOrEmpty(currentConversationId))
                {
                    if (_options.IncludeReasoningContent)
                    {
                        transcriptThinking = await _transcriptReader.ExtractThinkingAsync(currentConversationId, cancellationToken);
                    }
                    transcriptContent = await _transcriptReader.ExtractLatestResponseAsync(currentConversationId, cancellationToken);
                }

                var finalResp = responseBuilder.Length > 0
                    ? responseBuilder.ToString()
                    : transcriptContent;

                yield return new AgyStreamItem
                {
                    ConversationId = currentConversationId,
                    IsComplete = true,
                    FullResponse = finalResp,
                    Thinking = transcriptThinking,
                    Usage = lastUsage
                };
            }
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await process.WaitForExitAsync(reapCts.Token);
                    }
                }
                catch
                {
                    // ignored
                }

                if (writeInputTask != null)
                {
                    try { await writeInputTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                }

                if (readStderrTask != null)
                {
                    try { await readStderrTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                }

                process.Dispose();
            }
        }
    }

    private static async Task<string> ReadBoundedStderrAsync(StreamReader stderr, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync(cancellationToken)) != null)
            {
                if (sb.Length < MaxStderrChars)
                {
                    sb.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch
        {
            // ignored
        }
        return sb.ToString().Trim();
    }

    public async Task<AgyStreamItem> ExecutePromptAsync(
        AgyExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        string? conversationId = request.ConversationId;
        var fullText = new StringBuilder();
        string? thinking = null;
        AgyUsage? usage = null;
        string? error = null;

        await foreach (var item in ExecutePromptStreamAsync(request, cancellationToken))
        {
            if (!string.IsNullOrEmpty(item.ConversationId))
            {
                conversationId = item.ConversationId;
            }

            if (!string.IsNullOrEmpty(item.TextDelta))
            {
                fullText.Append(item.TextDelta);
            }

            if (!string.IsNullOrEmpty(item.Thinking))
            {
                thinking = item.Thinking;
            }

            if (item.Usage != null)
            {
                usage = item.Usage;
            }

            if (item.IsComplete && !string.IsNullOrEmpty(item.FullResponse))
            {
                fullText.Clear();
                fullText.Append(item.FullResponse);
            }

            if (!string.IsNullOrEmpty(item.Error))
            {
                error = item.Error;
            }
        }

        if (_options.IncludeReasoningContent && string.IsNullOrEmpty(thinking) && !string.IsNullOrEmpty(conversationId))
        {
            thinking = await _transcriptReader.ExtractThinkingAsync(conversationId, cancellationToken);
        }

        return new AgyStreamItem
        {
            ConversationId = conversationId,
            FullResponse = fullText.ToString(),
            Thinking = thinking,
            Usage = usage,
            IsComplete = true,
            Error = error
        };
    }
}
