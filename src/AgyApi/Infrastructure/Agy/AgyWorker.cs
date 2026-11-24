using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgyApi.Configuration;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Agy;

public class AgyWorker : IAgyWorker
{
    private readonly string _id;
    private readonly string _model;
    private readonly string _effort;
    private readonly string? _initialConversationId;
    private readonly bool _enableAgentic;
    private readonly AgyOptions _options;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly ILogger<AgyWorker> _logger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly object _stderrLock = new();
    private readonly StringBuilder _recentStderr = new();

    private Process? _process;
    private int _processId;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _currentConversationId;
    private int _turnCount;
    private readonly DateTime _createdUtc;
    private DateTime _lastActiveUtc;
    private volatile bool _isHealthy;
    private volatile bool _isBusy;
    private int _disposed;

    public string Id => _id;
    public int ProcessId => Volatile.Read(ref _processId);
    public string? CurrentConversationId => _currentConversationId;
    public string Model => _model;
    public string Effort => _effort;
    public bool EnableAgenticMode => _enableAgentic;
    public int TurnCount => Volatile.Read(ref _turnCount);
    public DateTime CreatedUtc => _createdUtc;
    public DateTime LastActiveUtc => _lastActiveUtc;
    public bool IsHealthy => Volatile.Read(ref _disposed) == 0 && _isHealthy && IsProcessAlive();
    public bool IsBusy => _isBusy;

    public long WorkingSetBytes
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            var proc = Volatile.Read(ref _process);
            if (proc == null) return 0;

            try
            {
                if (!proc.HasExited)
                {
                    proc.Refresh();
                    return proc.WorkingSet64;
                }
            }
            catch
            {
                // Process exited or disposed concurrently
            }
            return 0;
        }
    }

    public AgyWorker(
        string id,
        string model,
        string effort,
        string? conversationId,
        IOptions<AgyOptions> options,
        IAgyTranscriptReader transcriptReader,
        ILogger<AgyWorker> logger,
        bool enableAgentic = false)
    {
        _id = id;
        _model = model;
        _effort = effort;
        _initialConversationId = conversationId;
        _options = options.Value;
        _enableAgentic = enableAgentic && _options.AllowAgenticTools;
        _transcriptReader = transcriptReader;
        _logger = logger;
        _createdUtc = DateTime.UtcNow;
        _lastActiveUtc = DateTime.UtcNow;
    }

    private bool IsProcessAlive()
    {
        var proc = Volatile.Read(ref _process);
        if (proc == null) return false;

        try
        {
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var psi = AgyProcessHelpers.BuildCliProcessStartInfo(
            _options,
            _model,
            _effort,
            _initialConversationId,
            _enableAgentic,
            streamJsonInput: true);

        _logger.LogInformation("Starting persistent AgyWorker {Id} for model {Model}, effort {Effort}, agentic {Agentic}",
            _id, _model, _effort, _enableAgentic);

        var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                AppendStderr(e.Data);
                _logger.LogWarning("[Worker {Id} stderr] {Data}", _id, e.Data);
            }
        };

        try
        {
            proc.Start();
        }
        catch
        {
            proc.Dispose();
            throw;
        }

        Volatile.Write(ref _process, proc);
        Volatile.Write(ref _processId, proc.Id);
        proc.BeginErrorReadLine();

        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;

        try
        {
            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _workerCts.Token);
            initCts.CancelAfter(TimeSpan.FromSeconds(20));

            bool receivedInit = false;
            while (!initCts.Token.IsCancellationRequested)
            {
                var line = await _stdout.ReadLineAsync(initCts.Token);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("conversation_id", out var convProp))
                    {
                        _currentConversationId = convProp.GetString();
                    }

                    if (doc.RootElement.TryGetProperty("event", out var eventProp) &&
                        string.Equals(eventProp.GetString(), "init", StringComparison.OrdinalIgnoreCase))
                    {
                        receivedInit = true;
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(_currentConversationId))
                    {
                        receivedInit = true;
                        break;
                    }
                }
                catch (JsonException)
                {
                    _logger.LogDebug("[Worker {Id}] Non-JSON startup line: {Line}", _id, line);
                }
            }

            if (!receivedInit || proc.HasExited)
            {
                try
                {
                    using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    exitCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await proc.WaitForExitAsync(exitCts.Token);
                }
                catch
                {
                    // ignored
                }

                var exitInfo = proc.HasExited ? $"exit code {proc.ExitCode}" : "unexpected stdout EOF";
                var errText = GetRecentStderr();
                var message = string.IsNullOrWhiteSpace(errText)
                    ? $"AGY worker {_id} failed to initialize ({exitInfo})."
                    : $"AGY worker {_id} failed to initialize ({exitInfo}): {errText}";

                throw new InvalidOperationException(message);
            }

            _isHealthy = true;
            _logger.LogInformation("Persistent AgyWorker {Id} initialized with PID {Pid}, conversation {Conv}, agentic {Agentic}",
                _id, _processId, _currentConversationId ?? "none", _enableAgentic);
        }
        catch (Exception ex)
        {
            _isHealthy = false;
            _logger.LogError(ex, "Failed to initialize AgyWorker {Id} on PID {Pid}", _id, _processId);
            await TerminateAsync();
            throw;
        }
    }

    public async IAsyncEnumerable<AgyStreamItem> ExecuteTurnStreamAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsHealthy || _stdin == null || _stdout == null)
        {
            yield return new AgyStreamItem
            {
                IsComplete = true,
                Error = $"Worker {_id} is not healthy or has exited."
            };
            yield break;
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _workerCts.Token);
        turnCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.PrintTimeoutMinutes)));
        var turnToken = turnCts.Token;

        await _turnLock.WaitAsync(turnToken);
        _isBusy = true;
        _lastActiveUtc = DateTime.UtcNow;

        var responseBuilder = new StringBuilder();
        AgyUsage? lastUsage = null;
        var hasReceivedResult = false;
        var turnCompletedNormally = false;

        try
        {
            var inputEvent = new
            {
                @event = "user",
                message = new
                {
                    content = prompt
                }
            };

            var jsonLine = JsonSerializer.Serialize(inputEvent);
            await _stdin.WriteLineAsync(jsonLine.AsMemory(), turnToken);
            await _stdin.FlushAsync(turnToken);

            string? line;
            while ((line = await _stdout.ReadLineAsync(turnToken)) != null)
            {
                if (turnToken.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                AgyRawEvent? rawEvent = null;
                try
                {
                    rawEvent = JsonSerializer.Deserialize<AgyRawEvent>(line);
                }
                catch (JsonException)
                {
                    _logger.LogDebug("[Worker {Id}] Non-JSON line: {Line}", _id, line);
                    continue;
                }

                if (rawEvent == null) continue;

                _logger.LogTrace("[Worker {Id}] Read line ({Len} chars): Event={Event}",
                    _id, line.Length, rawEvent.Event);

                if (!string.IsNullOrEmpty(rawEvent.ConversationId))
                {
                    _currentConversationId = rawEvent.ConversationId;
                }

                switch (rawEvent.Event)
                {
                    case "init":
                        yield return new AgyStreamItem
                        {
                            ConversationId = _currentConversationId
                        };
                        break;

                    case "step_update":
                        var step = rawEvent.StepUpdate;
                        if (step != null)
                        {
                            if (!string.IsNullOrEmpty(step.ConversationId))
                            {
                                _currentConversationId = step.ConversationId;
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
                                    ConversationId = _currentConversationId,
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
                            Interlocked.Increment(ref _turnCount);
                            _lastActiveUtc = DateTime.UtcNow;

                            if (!string.IsNullOrEmpty(result.ConversationId))
                            {
                                _currentConversationId = result.ConversationId;
                            }
                            if (result.Usage != null)
                            {
                                lastUsage = result.Usage;
                            }

                            string? transcriptThinking = null;
                            if (_options.IncludeReasoningContent && !string.IsNullOrEmpty(_currentConversationId))
                            {
                                transcriptThinking = await _transcriptReader.ExtractThinkingAsync(_currentConversationId, turnToken);
                            }

                            yield return new AgyStreamItem
                            {
                                ConversationId = _currentConversationId,
                                IsComplete = true,
                                FullResponse = !string.IsNullOrEmpty(result.Response) ? result.Response : responseBuilder.ToString(),
                                Thinking = transcriptThinking,
                                Usage = lastUsage,
                                Error = result.Error
                            };
                        }
                        break;
                }

                if (hasReceivedResult)
                {
                    turnCompletedNormally = true;
                    break;
                }
            }

            if (!hasReceivedResult)
            {
                _isHealthy = false;
                var errText = GetRecentStderr();
                var message = string.IsNullOrWhiteSpace(errText)
                    ? $"Worker {_id} reached stream EOF without returning a turn result."
                    : $"Worker {_id} reached stream EOF without returning a turn result: {errText}";

                yield return new AgyStreamItem
                {
                    ConversationId = _currentConversationId,
                    IsComplete = true,
                    Error = message
                };
            }
        }
        finally
        {
            if (!turnCompletedNormally)
            {
                _isHealthy = false;
            }
            _isBusy = false;
            _turnLock.Release();
        }
    }

    private void AppendStderr(string line)
    {
        lock (_stderrLock)
        {
            if (_recentStderr.Length > 4096)
            {
                _recentStderr.Remove(0, _recentStderr.Length - 2048);
            }
            _recentStderr.AppendLine(line);
        }
    }

    private string GetRecentStderr()
    {
        lock (_stderrLock)
        {
            return _recentStderr.ToString().Trim();
        }
    }

    public async Task TerminateAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _isHealthy = false;

        _logger.LogInformation("Terminating AgyWorker {Id} (PID {Pid})", _id, ProcessId);

        try
        {
            await _workerCts.CancelAsync();
        }
        catch
        {
            // ignored
        }

        try
        {
            _stdin?.Close();
        }
        catch
        {
            // ignored
        }

        var proc = Interlocked.Exchange(ref _process, null);
        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    using var reapCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await proc.WaitForExitAsync(reapCts.Token);
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                proc.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        _workerCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await TerminateAsync();
        GC.SuppressFinalize(this);
    }
}
