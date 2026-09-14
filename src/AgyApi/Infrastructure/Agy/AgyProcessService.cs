using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgyApi.Configuration;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Agy;

public class AgyProcessService : IAgyProcessService
{
    private readonly AgyOptions _options;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly IAgyWorkerPool _workerPool;
    private readonly ILogger<AgyProcessService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.OrdinalIgnoreCase);

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
        var conversationLock = string.IsNullOrWhiteSpace(request.ConversationId)
            ? null
            : _conversationLocks.GetOrAdd(request.ConversationId, _ => new SemaphoreSlim(1, 1));

        if (conversationLock != null)
        {
            await conversationLock.WaitAsync(cancellationToken);
        }

        if (_options.EnableWorkerPool)
        {
            var effort = !string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? request.ReasoningEffort
                : _options.DefaultReasoningEffort;

            IAgyWorker? worker = null;
            try
            {
                worker = await _workerPool.AcquireWorkerAsync(
                    request.ConversationId,
                    request.Model,
                    effort,
                    cancellationToken);

                await foreach (var item in worker.ExecuteTurnStreamAsync(request.Prompt, cancellationToken))
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
                conversationLock?.Release();
            }
            yield break;
        }

        Process? process = null;
        try
        {
            var workspaceDir = Path.GetFullPath(_options.WorkspaceDirectory);
            if (!Directory.Exists(workspaceDir))
            {
                Directory.CreateDirectory(workspaceDir);
            }

            var exePath = _options.ExecutablePath;
            if (string.Equals(exePath, "agy", StringComparison.OrdinalIgnoreCase) || string.Equals(exePath, "agy.exe", StringComparison.OrdinalIgnoreCase))
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var candidate = Path.Combine(localAppData, "agy", "bin", "agy.exe");
                if (File.Exists(candidate))
                {
                    exePath = candidate;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workspaceDir
            };

            // NOTE: Do NOT use --print or pass request.Prompt on the command line!
            // Windows Win32 CreateProcess limits the command-line string to 32,767 characters.
            // Complex client contexts (e.g. Hermes Gateway with conversation history and tools) exceed this,
            // resulting in Win32Exception 206 (The filename or extension is too long).
            // Passing the prompt via StandardInput has no length limitations.
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("stream-json");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(request.Model);

            var hasEffortTier = request.Model.EndsWith("-high", StringComparison.OrdinalIgnoreCase) ||
                                request.Model.EndsWith("-medium", StringComparison.OrdinalIgnoreCase) ||
                                request.Model.EndsWith("-low", StringComparison.OrdinalIgnoreCase);

            var isClaude = request.Model.Contains("claude", StringComparison.OrdinalIgnoreCase);

            var effort = !string.IsNullOrWhiteSpace(request.ReasoningEffort)
                ? request.ReasoningEffort
                : _options.DefaultReasoningEffort;

            if (!hasEffortTier && !isClaude && !string.IsNullOrWhiteSpace(effort))
            {
                psi.ArgumentList.Add("--effort");
                psi.ArgumentList.Add(effort.ToLowerInvariant());
            }

            if (!string.IsNullOrWhiteSpace(request.ConversationId))
            {
                psi.ArgumentList.Add("--conversation");
                psi.ArgumentList.Add(request.ConversationId);
            }

            // In headless API server mode, always auto-approve tool execution permissions
            // to prevent process halts on stdin review prompts, and disable interactive slash commands
            psi.ArgumentList.Add("--dangerously-skip-permissions");
            psi.ArgumentList.Add("--disable-slash-commands");
            psi.ArgumentList.Add("--print-timeout");
            psi.ArgumentList.Add($"{_options.PrintTimeoutMinutes}m0s");

            _logger.LogInformation("Spawning AGY process for model: {Model}, effort: {Effort}, agentic: {Agentic}, conversation: {ConvId}, promptLength: {PromptLen}",
                request.Model, effort, request.EnableAgenticMode, request.ConversationId ?? "new", request.Prompt.Length);

            process = new Process { StartInfo = psi };
            var stderrBuilder = new StringBuilder();

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    stderrBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            // Asynchronously stream prompt into StandardInput and close to signal EOF
            var writeInputTask = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(request.Prompt);
                    await process.StandardInput.FlushAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to write prompt to AGY StandardInput");
                }
                finally
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            });

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
                            if (!string.IsNullOrEmpty(currentConversationId))
                            {
                                transcriptThinking = await _transcriptReader.ExtractThinkingAsync(currentConversationId, cancellationToken);
                            }

                            yield return new AgyStreamItem
                            {
                                ConversationId = currentConversationId,
                                IsComplete = true,
                                FullResponse = result.Response ?? responseBuilder.ToString(),
                                Thinking = transcriptThinking,
                                Usage = lastUsage
                            };
                        }
                        break;
                }
            }

            try
            {
                await writeInputTask;
            }
            catch
            {
                // ignored
            }

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var err = stderrBuilder.ToString().Trim();
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
                    transcriptThinking = await _transcriptReader.ExtractThinkingAsync(currentConversationId, cancellationToken);
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
                    }
                }
                catch
                {
                    // ignored
                }
                process.Dispose();
            }

            conversationLock?.Release();
        }
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

        if (string.IsNullOrEmpty(thinking) && !string.IsNullOrEmpty(conversationId))
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
