using System.Text;

namespace AgyApi.Features.ChatCompletions;

public class ThoughtStreamProcessor : IThoughtStreamProcessor
{
    private static readonly string[] StartTags = ["<think>", "<thought>", "<reasoning>"];
    private static readonly string[] EndTags = ["</think>", "</thought>", "</reasoning>"];
    private const int MaxStartTagLen = 11; // "<reasoning>".Length
    private const int MaxEndTagLen = 12;   // "</reasoning>".Length

    private readonly bool _emitTagsInContent;
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _allThinking = new();
    private readonly StringBuilder _allContent = new();
    private readonly StringBuilder _contentOut = new();
    private readonly StringBuilder _reasoningOut = new();
    private bool _insideThink;

    public bool HasSeenThinking => _allThinking.Length > 0 || _insideThink;
    public bool IsInsideThink => _insideThink;

    public ThoughtStreamProcessor(bool emitTagsInContent = false)
    {
        _emitTagsInContent = emitTagsInContent;
    }

    public ProcessedChunk Process(string incomingDelta)
    {
        if (string.IsNullOrEmpty(incomingDelta))
        {
            return new ProcessedChunk(null, null, _insideThink);
        }

        string text;
        if (_buffer.Length == 0)
        {
            text = incomingDelta;
        }
        else
        {
            _buffer.Append(incomingDelta);
            text = _buffer.ToString();
            _buffer.Clear();
        }

        _contentOut.Clear();
        _reasoningOut.Clear();

        ReadOnlySpan<char> remaining = text.AsSpan();

        while (!remaining.IsEmpty)
        {
            if (!_insideThink)
            {
                int thinkStart = IndexOfTag(remaining, StartTags, out int tagLen);
                if (thinkStart >= 0)
                {
                    if (thinkStart > 0)
                    {
                        var before = remaining[..thinkStart];
                        _contentOut.Append(before);
                        _allContent.Append(before);
                    }

                    _insideThink = true;
                    if (_emitTagsInContent)
                    {
                        _contentOut.Append(remaining.Slice(thinkStart, tagLen));
                    }

                    remaining = remaining[(thinkStart + tagLen)..];
                }
                else
                {
                    int partialIndex = CheckPartialTag(remaining, StartTags, MaxStartTagLen);
                    if (partialIndex >= 0)
                    {
                        if (partialIndex > 0)
                        {
                            var safe = remaining[..partialIndex];
                            _contentOut.Append(safe);
                            _allContent.Append(safe);
                        }
                        _buffer.Append(remaining[partialIndex..]);
                        break;
                    }

                    _contentOut.Append(remaining);
                    _allContent.Append(remaining);
                    break;
                }
            }
            else
            {
                int thinkEnd = IndexOfTag(remaining, EndTags, out int tagLen);
                if (thinkEnd >= 0)
                {
                    if (thinkEnd > 0)
                    {
                        var thoughtChunk = remaining[..thinkEnd];
                        _reasoningOut.Append(thoughtChunk);
                        _allThinking.Append(thoughtChunk);

                        if (_emitTagsInContent)
                        {
                            _contentOut.Append(thoughtChunk);
                        }
                    }

                    _insideThink = false;
                    if (_emitTagsInContent)
                    {
                        _contentOut.Append(remaining.Slice(thinkEnd, tagLen));
                    }

                    remaining = remaining[(thinkEnd + tagLen)..];
                }
                else
                {
                    int partialEnd = CheckPartialTag(remaining, EndTags, MaxEndTagLen);
                    if (partialEnd >= 0)
                    {
                        if (partialEnd > 0)
                        {
                            var safe = remaining[..partialEnd];
                            _reasoningOut.Append(safe);
                            _allThinking.Append(safe);

                            if (_emitTagsInContent)
                            {
                                _contentOut.Append(safe);
                            }
                        }
                        _buffer.Append(remaining[partialEnd..]);
                        break;
                    }

                    _reasoningOut.Append(remaining);
                    _allThinking.Append(remaining);

                    if (_emitTagsInContent)
                    {
                        _contentOut.Append(remaining);
                    }
                    break;
                }
            }
        }

        return new ProcessedChunk(
            _contentOut.Length > 0 ? _contentOut.ToString() : null,
            _reasoningOut.Length > 0 ? _reasoningOut.ToString() : null,
            _insideThink);
    }

    public ProcessedChunk Flush()
    {
        if (_buffer.Length == 0)
        {
            return new ProcessedChunk(null, null, _insideThink);
        }

        var remaining = _buffer.ToString();
        _buffer.Clear();

        if (_insideThink)
        {
            _allThinking.Append(remaining);
            return new ProcessedChunk(_emitTagsInContent ? remaining : null, remaining, _insideThink);
        }

        _allContent.Append(remaining);
        return new ProcessedChunk(remaining, null, _insideThink);
    }

    public string GetFullThinking() => _allThinking.ToString().Trim();
    public string GetFullContent() => _allContent.ToString().Trim();

    private static int IndexOfTag(ReadOnlySpan<char> span, string[] tags, out int matchedLength)
    {
        int minIdx = -1;
        matchedLength = 0;

        foreach (var tag in tags)
        {
            int idx = span.IndexOf(tag.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (minIdx < 0 || idx < minIdx))
            {
                minIdx = idx;
                matchedLength = tag.Length;
            }
        }

        return minIdx;
    }

    private static int CheckPartialTag(ReadOnlySpan<char> span, string[] candidateTags, int maxTagLen)
    {
        // Check longest possible suffix first; allow len == span.Length so 1-char "<" tokens are buffered
        int maxCheck = Math.Min(span.Length, maxTagLen - 1);
        for (int len = maxCheck; len >= 1; len--)
        {
            var tail = span[^len..];
            foreach (var tag in candidateTags)
            {
                if (len < tag.Length && tag.AsSpan().StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    return span.Length - len;
                }
            }
        }

        return -1;
    }
}
