using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AgyApi.Infrastructure.Agy;

public class AgyTranscriptReader : IAgyTranscriptReader
{
    private const int ChunkSize = 32 * 1024;
    private readonly ILogger<AgyTranscriptReader> _logger;
    private readonly string[] _brainBaseDirs;

    [ActivatorUtilitiesConstructor]
    public AgyTranscriptReader(ILogger<AgyTranscriptReader> logger)
        : this(logger, GetDefaultBrainBaseDirs())
    {
    }

    internal AgyTranscriptReader(ILogger<AgyTranscriptReader> logger, params string[] brainBaseDirs)
    {
        _logger = logger;
        _brainBaseDirs = brainBaseDirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(Path.GetFullPath)
            .ToArray();
    }

    private static string[] GetDefaultBrainBaseDirs()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "brain"),
            Path.Combine(userProfile, ".gemini", "antigravity", "brain")
        ];
    }

    public Task<string?> ExtractThinkingAsync(string conversationId, CancellationToken cancellationToken = default)
        => ExtractLatestFieldAsync(conversationId, "thinking", cancellationToken);

    public Task<string?> ExtractLatestResponseAsync(string conversationId, CancellationToken cancellationToken = default)
        => ExtractLatestFieldAsync(conversationId, "content", cancellationToken);

    private async Task<string?> ExtractLatestFieldAsync(
        string conversationId,
        string propertyName,
        CancellationToken cancellationToken)
    {
        if (!AgyProcessHelpers.IsValidConversationId(conversationId))
        {
            _logger.LogWarning("Rejected invalid conversationId for transcript lookup.");
            return null;
        }

        foreach (var baseDir in _brainBaseDirs)
        {
            var normalizedBase = baseDir.EndsWith(Path.DirectorySeparatorChar)
                ? baseDir
                : baseDir + Path.DirectorySeparatorChar;

            foreach (var fileName in new[] { "transcript_full.jsonl", "transcript.jsonl" })
            {
                var candidatePath = Path.GetFullPath(
                    Path.Combine(baseDir, conversationId, ".system_generated", "logs", fileName));

                // Path-jail verification
                if (!candidatePath.StartsWith(normalizedBase, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Blocked transcript path traversal attempt for conversationId {ConvId}", conversationId);
                    return null;
                }

                if (!File.Exists(candidatePath))
                    continue;

                try
                {
                    var extracted = await ScanReverseForLatestFieldAsync(candidatePath, propertyName, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        _logger.LogDebug("Extracted {Property} ({Length} chars) from transcript {Path}",
                            propertyName, extracted.Length, candidatePath);
                        return extracted;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed reading transcript file at {Path}", candidatePath);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Scans a JSONL file backwards from EOF in 32 KB chunks, splitting on UTF-8 newline bytes (0x0A)
    /// and stopping immediately upon reaching the current turn's USER_INPUT boundary.
    /// </summary>
    private static async Task<string?> ScanReverseForLatestFieldAsync(
        string path,
        string propertyName,
        CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: ChunkSize,
            FileOptions.Asynchronous);

        long position = fs.Length;
        if (position == 0) return null;

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[] carry = [];

        try
        {
            while (position > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(ChunkSize, position);
                position -= toRead;
                fs.Seek(position, SeekOrigin.Begin);

                int bytesRead = 0;
                while (bytesRead < toRead)
                {
                    int read = await fs.ReadAsync(readBuffer.AsMemory(bytesRead, toRead - bytesRead), cancellationToken);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead == 0) break;

                int segmentEnd = bytesRead;
                bool usedCarry = false;

                for (int i = bytesRead - 1; i >= 0; i--)
                {
                    if (readBuffer[i] == (byte)'\n')
                    {
                        var lineBytes = BuildLineBytes(readBuffer, i + 1, segmentEnd - (i + 1), !usedCarry ? carry : []);
                        usedCarry = true;
                        segmentEnd = i;

                        if (TryInspectLine(lineBytes, propertyName, out var foundValue, out var hitUserBoundary))
                        {
                            return foundValue;
                        }
                        if (hitUserBoundary)
                        {
                            return null;
                        }
                    }
                }

                // Remaining prefix [0..segmentEnd) is either the start of a line split across chunks
                // or the very first line of the file (when position == 0)
                var prefixBytes = BuildLineBytes(readBuffer, 0, segmentEnd, !usedCarry ? carry : []);

                if (position == 0)
                {
                    if (TryInspectLine(prefixBytes, propertyName, out var foundValue, out _))
                    {
                        return foundValue;
                    }
                    break;
                }

                carry = prefixBytes;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }

        return null;
    }

    private static byte[] BuildLineBytes(byte[] buffer, int offset, int count, byte[] suffix)
    {
        if (count == 0 && suffix.Length == 0) return [];
        if (suffix.Length == 0)
        {
            var result = new byte[count];
            Buffer.BlockCopy(buffer, offset, result, 0, count);
            return result;
        }
        if (count == 0)
        {
            return suffix;
        }

        var combined = new byte[count + suffix.Length];
        Buffer.BlockCopy(buffer, offset, combined, 0, count);
        Buffer.BlockCopy(suffix, 0, combined, count, suffix.Length);
        return combined;
    }

    private static bool TryInspectLine(byte[] lineBytes, string propertyName, out string? foundValue, out bool hitUserBoundary)
    {
        foundValue = null;
        hitUserBoundary = false;

        if (lineBytes.Length == 0) return false;

        // Trim trailing CR/whitespace
        int end = lineBytes.Length;
        while (end > 0 && (lineBytes[end - 1] == (byte)'\r' || lineBytes[end - 1] == (byte)' ' || lineBytes[end - 1] == (byte)'\t'))
        {
            end--;
        }
        int start = 0;
        while (start < end && (lineBytes[start] == (byte)' ' || lineBytes[start] == (byte)'\t'))
        {
            start++;
        }
        if (start >= end) return false;

        try
        {
            var lineSpan = lineBytes.AsMemory(start, end - start);
            using var doc = JsonDocument.Parse(lineSpan);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var typeProp))
            {
                var typeStr = typeProp.GetString();
                if (typeStr == "PLANNER_RESPONSE" &&
                    root.TryGetProperty(propertyName, out var prop) &&
                    prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        foundValue = val.Trim();
                        return true;
                    }
                }
                else if (typeStr == "USER_INPUT")
                {
                    hitUserBoundary = true;
                    return false;
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed individual line
        }

        return false;
    }
}
