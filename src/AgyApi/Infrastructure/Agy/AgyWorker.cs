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
    private readonly AgyOptions _options;
    private readonly IAgyTranscriptReader _transcriptReader;
    private readonly ILogger<AgyWorker> _logger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _currentConversationId;
    private int _turnCount;
    private DateTime _createdUtc;
    private DateTime _lastActiveUtc;
    private volatile bool _isHealthy;
    private volatile bool _isBusy;
    private bool _disposed;

    public string Id => _id;
    public int ProcessId => _process?.Id ?? 0;
    public string? CurrentConversationId => _currentConversationId;
    public string Model => _model;
    public string Effort => _effort;
    public int TurnCount => _turnCount;
    public DateTime CreatedUtc => _createdUtc;
    public DateTime LastActiveUtc => _lastActiveUtc;
    public bool IsHealthy => _isHealthy && _process != null && !_process.HasExited;
    public bool IsBusy => _isBusy;

    public long WorkingSetBytes
    {
        get
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Refresh();
                    return _process.WorkingSet64;
                }
            }
            catch
            {
                // ignored
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
        ILogger<AgyWorker> logger)
    {
        _id = id;
        _model = model;
        _effort = effort;
        _initialConversationId = conversationId;
        _options = options.Value;
        _transcriptReader = transcriptReader;
        _logger = logger;
        _createdUtc = DateTime.UtcNow;
        _lastActiveUtc = DateTime.UtcNow;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var workspaceDir = Path.GetFullPath(_options.WorkspaceDirectory);
        if (!Directory.Exists(workspaceDir))
        {
            Directory.CreateDirectory(workspaceDir);
        }

        var exePath = _options.ExecutablePath;
        if (string.Equals(exePath, "agy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exePath, "agy.exe", StringComparison.OrdinalIgnoreCase))
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
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workspaceDir
        };

        psi.ArgumentList.Add("--input-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_model);

        var hasEffortTier = _model.EndsWith("-high", StringComparison.OrdinalIgnoreCase) ||
                            _model.EndsWith("-medium", StringComparison.OrdinalIgnoreCase) ||
                            _model.EndsWith("-low", StringComparison.OrdinalIgnoreCase);

        var isClaude = _model.Contains("claude", StringComparison.OrdinalIgnoreCase);

        if (!hasEffortTier && !isClaude && !string.IsNullOrWhiteSpace(_effort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(_effort.ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(_initialConversationId))
        {
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(_initialConversationId);
        }

        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--disable-slash-commands");
        psi.ArgumentList.Add("--print-timeout");
        psi.ArgumentList.Add($"{_options.PrintTimeoutMinutes}m0s");

        _logger.LogInformation("Starting persistent AgyWorker {Id} for model {Model}, effort {Effort}",
            _id, _model, _effort);

        _process = new Process { StartInfo = psi };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogWarning("[Worker {Id} stderr] {Data}", _id, e.Data);
            }
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        try
        {
            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            initCts.CancelAfter(TimeSpan.FromSeconds(20));

            var firstLine = await _stdout.ReadLineAsync(initCts.Token);
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                using var doc = JsonDocument.Parse(firstLine);
                if (doc.RootElement.TryGetProperty("conversation_id", out var convProp))
                {
                    _currentConversationId = convProp.GetString();
                }
            }

            _isHealthy = true;
            _logger.LogInformation("Persistent AgyWorker {Id} successfully initialized with PID {Pid}, conversation {Conv}",
                _id, _process.Id, _currentConversationId ?? "none");
        }
        catch (Exception ex)
        {
            _isHealthy = false;
            _logger.LogError(ex, "Failed to initialize AgyWorker {Id} on PID {Pid}", _id, _process.Id);
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

        await _turnLock.WaitAsync(cancellationToken);
        _isBusy = true;
        _lastActiveUtc = DateTime.UtcNow;

        var responseBuilder = new StringBuilder();
        AgyUsage? lastUsage = null;
        var hasReceivedResult = false;

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
            await _stdin.WriteLineAsync(jsonLine.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);

            string? line;
            while ((line = await _stdout.ReadLineAsync(cancellationToken)) != null)
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
                    _logger.LogDebug("[Worker {Id}] Non-JSON line: {Line}", _id, line);
                    continue;
                }

                if (rawEvent == null) continue;

                _logger.LogInformation("[Worker {Id}] Read line ({Len} chars): Event={Event}",
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
                            _turnCount++;
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
                            if (!string.IsNullOrEmpty(_currentConversationId))
                            {
                                transcriptThinking = await _transcriptReader.ExtractThinkingAsync(_currentConversationId, cancellationToken);
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
                    break;
                }
            }

            if (!hasReceivedResult)
            {
                _isHealthy = false;
                yield return new AgyStreamItem
                {
                    ConversationId = _currentConversationId,
                    IsComplete = true,
                    Error = $"Worker {_id} reached stream EOF without returning a turn result."
                };
            }
        }
        finally
        {
            _isBusy = false;
            _turnLock.Release();
        }
    }

    public async Task TerminateAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _isHealthy = false;

        _logger.LogInformation("Terminating AgyWorker {Id} (PID {Pid})", _id, ProcessId);

        try
        {
            if (_stdin != null)
            {
                _stdin.Close();
            }
        }
        catch
        {
            // ignored
        }

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                _process.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        _turnLock.Dispose();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await TerminateAsync();
        GC.SuppressFinalize(this);
    }
}
