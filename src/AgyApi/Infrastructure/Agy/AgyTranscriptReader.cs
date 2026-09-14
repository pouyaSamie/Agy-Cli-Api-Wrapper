using System.Text.Json;

namespace AgyApi.Infrastructure.Agy;

public class AgyTranscriptReader : IAgyTranscriptReader
{
    private readonly ILogger<AgyTranscriptReader> _logger;
    private readonly string _cliBrainBase;
    private readonly string _ideBrainBase;

    public AgyTranscriptReader(ILogger<AgyTranscriptReader> logger)
    {
        _logger = logger;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _cliBrainBase = Path.Combine(userProfile, ".gemini", "antigravity-cli", "brain");
        _ideBrainBase = Path.Combine(userProfile, ".gemini", "antigravity", "brain");
    }

    public async Task<string?> ExtractThinkingAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var potentialPaths = new[]
        {
            Path.Combine(_cliBrainBase, conversationId, ".system_generated", "logs", "transcript_full.jsonl"),
            Path.Combine(_cliBrainBase, conversationId, ".system_generated", "logs", "transcript.jsonl"),
            Path.Combine(_ideBrainBase, conversationId, ".system_generated", "logs", "transcript_full.jsonl"),
            Path.Combine(_ideBrainBase, conversationId, ".system_generated", "logs", "transcript.jsonl")
        };

        foreach (var path in potentialPaths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);

                var lines = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }

                // Search from bottom up for the latest PLANNER_RESPONSE with thinking
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(lines[i]);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("type", out var typeProp))
                        {
                            var typeStr = typeProp.GetString();
                            if (typeStr == "PLANNER_RESPONSE" &&
                                root.TryGetProperty("thinking", out var thinkingProp))
                            {
                                var thinking = thinkingProp.GetString();
                                if (!string.IsNullOrWhiteSpace(thinking))
                                {
                                    _logger.LogDebug("Extracted thinking ({Length} chars) from transcript {Path}", thinking.Length, path);
                                    return thinking.Trim();
                                }
                            }
                            else if (typeStr == "USER_INPUT")
                            {
                                // We reached the boundary of the current turn; do not leak thinking from earlier turns
                                break;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore individual malformed line
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed reading transcript file at {Path}", path);
            }
        }

        return null;
    }

    public async Task<string?> ExtractLatestResponseAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var potentialPaths = new[]
        {
            Path.Combine(_cliBrainBase, conversationId, ".system_generated", "logs", "transcript_full.jsonl"),
            Path.Combine(_cliBrainBase, conversationId, ".system_generated", "logs", "transcript.jsonl"),
            Path.Combine(_ideBrainBase, conversationId, ".system_generated", "logs", "transcript_full.jsonl"),
            Path.Combine(_ideBrainBase, conversationId, ".system_generated", "logs", "transcript.jsonl")
        };

        foreach (var path in potentialPaths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);

                var lines = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }

                // Search from bottom up for the latest PLANNER_RESPONSE with content
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(lines[i]);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("type", out var typeProp))
                        {
                            var typeStr = typeProp.GetString();
                            if (typeStr == "PLANNER_RESPONSE" &&
                                root.TryGetProperty("content", out var contentProp))
                            {
                                var content = contentProp.GetString();
                                if (!string.IsNullOrWhiteSpace(content))
                                {
                                    _logger.LogDebug("Extracted content ({Length} chars) from transcript {Path}", content.Length, path);
                                    return content.Trim();
                                }
                            }
                            else if (typeStr == "USER_INPUT")
                            {
                                break;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore individual malformed line
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed reading transcript file at {Path}", path);
            }
        }

        return null;
    }
}
