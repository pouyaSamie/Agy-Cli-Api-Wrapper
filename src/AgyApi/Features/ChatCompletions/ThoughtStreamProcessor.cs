using System.Text;

namespace AgyApi.Features.ChatCompletions;

public class ThoughtStreamProcessor : IThoughtStreamProcessor
{
    private readonly bool _emitTagsInContent;
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _allThinking = new();
    private readonly StringBuilder _allContent = new();
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

        _buffer.Append(incomingDelta);
        var text = _buffer.ToString();
        _buffer.Clear();

        string? contentOut = null;
        string? reasoningOut = null;

        while (!string.IsNullOrEmpty(text))
        {
            if (!_insideThink)
            {
                // Check for start of think/thought tag
                int thinkStart = IndexOfTag(text, out string startTag);

                if (thinkStart >= 0)
                {
                    // Emit any content before the tag
                    if (thinkStart > 0)
                    {
                        var before = text[..thinkStart];
                        contentOut = (contentOut ?? "") + before;
                        _allContent.Append(before);
                    }

                    _insideThink = true;
                    if (_emitTagsInContent)
                    {
                        contentOut = (contentOut ?? "") + startTag;
                    }

                    text = text[(thinkStart + startTag.Length)..];
                }
                else
                {
                    // Check if the end of text could be a partial start tag (e.g. "<th", "<think")
                    int partialIndex = CheckPartialStartTag(text);
                    if (partialIndex >= 0)
                    {
                        var safe = text[..partialIndex];
                        if (!string.IsNullOrEmpty(safe))
                        {
                            contentOut = (contentOut ?? "") + safe;
                            _allContent.Append(safe);
                        }
                        _buffer.Append(text[partialIndex..]);
                        break;
                    }

                    // No tag in sight, all normal content
                    contentOut = (contentOut ?? "") + text;
                    _allContent.Append(text);
                    text = string.Empty;
                }
            }
            else // _insideThink == true
            {
                // Check for end of think/thought tag
                int thinkEnd = IndexOfEndTag(text, out string endTag);

                if (thinkEnd >= 0)
                {
                    // Text before the closing tag is reasoning
                    if (thinkEnd > 0)
                    {
                        var thoughtChunk = text[..thinkEnd];
                        reasoningOut = (reasoningOut ?? "") + thoughtChunk;
                        _allThinking.Append(thoughtChunk);

                        if (_emitTagsInContent)
                        {
                            contentOut = (contentOut ?? "") + thoughtChunk;
                        }
                    }

                    _insideThink = false;
                    if (_emitTagsInContent)
                    {
                        contentOut = (contentOut ?? "") + endTag;
                    }

                    text = text[(thinkEnd + endTag.Length)..];
                }
                else
                {
                    // Check if end of text is a partial closing tag (e.g. "</th", "</think")
                    int partialEnd = CheckPartialEndTag(text);
                    if (partialEnd >= 0)
                    {
                        var safe = text[..partialEnd];
                        if (!string.IsNullOrEmpty(safe))
                        {
                            reasoningOut = (reasoningOut ?? "") + safe;
                            _allThinking.Append(safe);

                            if (_emitTagsInContent)
                            {
                                contentOut = (contentOut ?? "") + safe;
                            }
                        }
                        _buffer.Append(text[partialEnd..]);
                        break;
                    }

                    // Still completely inside thinking
                    reasoningOut = (reasoningOut ?? "") + text;
                    _allThinking.Append(text);

                    if (_emitTagsInContent)
                    {
                        contentOut = (contentOut ?? "") + text;
                    }

                    text = string.Empty;
                }
            }
        }

        return new ProcessedChunk(contentOut, reasoningOut, _insideThink);
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
        else
        {
            _allContent.Append(remaining);
            return new ProcessedChunk(remaining, null, _insideThink);
        }
    }

    public string GetFullThinking() => _allThinking.ToString().Trim();
    public string GetFullContent() => _allContent.ToString().Trim();

    private static int IndexOfTag(string text, out string matchedTag)
    {
        int idx1 = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        int idx2 = text.IndexOf("<thought>", StringComparison.OrdinalIgnoreCase);
        int idx3 = text.IndexOf("<reasoning>", StringComparison.OrdinalIgnoreCase);

        int minIdx = -1;
        matchedTag = string.Empty;

        if (idx1 >= 0)
        {
            minIdx = idx1;
            matchedTag = text.Substring(idx1, 7);
        }
        if (idx2 >= 0 && (minIdx < 0 || idx2 < minIdx))
        {
            minIdx = idx2;
            matchedTag = text.Substring(idx2, 9);
        }
        if (idx3 >= 0 && (minIdx < 0 || idx3 < minIdx))
        {
            minIdx = idx3;
            matchedTag = text.Substring(idx3, 11);
        }

        return minIdx;
    }

    private static int IndexOfEndTag(string text, out string matchedTag)
    {
        int idx1 = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        int idx2 = text.IndexOf("</thought>", StringComparison.OrdinalIgnoreCase);
        int idx3 = text.IndexOf("</reasoning>", StringComparison.OrdinalIgnoreCase);

        int minIdx = -1;
        matchedTag = string.Empty;

        if (idx1 >= 0)
        {
            minIdx = idx1;
            matchedTag = text.Substring(idx1, 8);
        }
        if (idx2 >= 0 && (minIdx < 0 || idx2 < minIdx))
        {
            minIdx = idx2;
            matchedTag = text.Substring(idx2, 10);
        }
        if (idx3 >= 0 && (minIdx < 0 || idx3 < minIdx))
        {
            minIdx = idx3;
            matchedTag = text.Substring(idx3, 12);
        }

        return minIdx;
    }

    private static int CheckPartialStartTag(string text)
    {
        var candidateTags = new[] { "<think>", "<thought>", "<reasoning>" };
        for (int i = 1; i < text.Length && i < 12; i++)
        {
            var tail = text[^i..];
            foreach (var tag in candidateTags)
            {
                if (tag.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    return text.Length - i;
                }
            }
        }
        return -1;
    }

    private static int CheckPartialEndTag(string text)
    {
        var candidateTags = new[] { "</think>", "</thought>", "</reasoning>" };
        for (int i = 1; i < text.Length && i < 13; i++)
        {
            var tail = text[^i..];
            foreach (var tag in candidateTags)
            {
                if (tag.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    return text.Length - i;
                }
            }
        }
        return -1;
    }
}
