using System.Text;
using System.Text.Json;

namespace AgyApi.Features.ChatCompletions;

public class ToolCallStreamProcessor
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";

    private readonly StringBuilder _tagBuffer = new(16);
    private readonly StringBuilder _toolJsonBuffer = new();
    private readonly StringBuilder _fullContent = new();
    private readonly StringBuilder _contentOut = new();
    private readonly List<ToolCall> _parsedToolCalls = [];

    private bool _insideToolCall;
    private int _matchIndex;
    private int _toolIndex;

    public IReadOnlyList<ToolCall> ToolCalls => _parsedToolCalls;
    public bool HasToolCalls => _parsedToolCalls.Count > 0;

    public (string ContentDelta, List<ToolCall>? NewToolCalls) Process(string textDelta)
    {
        if (string.IsNullOrEmpty(textDelta))
        {
            return (string.Empty, null);
        }

        _contentOut.Clear();
        List<ToolCall>? newCalls = null;

        for (int i = 0; i < textDelta.Length; i++)
        {
            char c = textDelta[i];
            string targetTag = _insideToolCall ? CloseTag : OpenTag;

            if (AsciiEqualsIgnoreCase(c, targetTag[_matchIndex]))
            {
                _tagBuffer.Append(c);
                _matchIndex++;

                if (_matchIndex == targetTag.Length)
                {
                    _matchIndex = 0;
                    _tagBuffer.Clear();

                    if (_insideToolCall)
                    {
                        _insideToolCall = false;
                        var json = _toolJsonBuffer.ToString().Trim();
                        _toolJsonBuffer.Clear();

                        var tc = ParseToolCallJson(json, _toolIndex);
                        if (tc != null)
                        {
                            _toolIndex++;
                            _parsedToolCalls.Add(tc);
                            newCalls ??= [];
                            newCalls.Add(tc);
                        }
                    }
                    else
                    {
                        _insideToolCall = true;
                    }
                }
            }
            else
            {
                if (_matchIndex > 0)
                {
                    if (_insideToolCall)
                    {
                        _toolJsonBuffer.Append(_tagBuffer);
                    }
                    else
                    {
                        _contentOut.Append(_tagBuffer);
                        _fullContent.Append(_tagBuffer);
                    }
                    _tagBuffer.Clear();
                    _matchIndex = 0;
                }

                // Handle repeated '<' prefix (e.g. "<<tool_call>" or "<</tool_call>")
                if (c == '<')
                {
                    _tagBuffer.Append(c);
                    _matchIndex = 1;
                }
                else if (_insideToolCall)
                {
                    _toolJsonBuffer.Append(c);
                }
                else
                {
                    _contentOut.Append(c);
                    _fullContent.Append(c);
                }
            }
        }

        return (_contentOut.Length > 0 ? _contentOut.ToString() : string.Empty, newCalls);
    }

    public (string ContentDelta, List<ToolCall>? NewToolCalls) Flush()
    {
        _contentOut.Clear();
        List<ToolCall>? newCalls = null;

        if (_insideToolCall)
        {
            _insideToolCall = false;
            if (_tagBuffer.Length > 0)
            {
                _toolJsonBuffer.Append(_tagBuffer);
                _tagBuffer.Clear();
            }
            _matchIndex = 0;

            var json = _toolJsonBuffer.ToString().Trim();
            _toolJsonBuffer.Clear();

            var tc = ParseToolCallJson(json, _toolIndex);
            if (tc != null)
            {
                _toolIndex++;
                _parsedToolCalls.Add(tc);
                newCalls ??= [];
                newCalls.Add(tc);
            }
        }
        else if (_tagBuffer.Length > 0)
        {
            _contentOut.Append(_tagBuffer);
            _fullContent.Append(_tagBuffer);
            _tagBuffer.Clear();
            _matchIndex = 0;
        }

        return (_contentOut.Length > 0 ? _contentOut.ToString() : string.Empty, newCalls);
    }

    public string GetFullContent() => _fullContent.ToString();

    private static bool AsciiEqualsIgnoreCase(char input, char lowercaseTarget)
    {
        if (input == lowercaseTarget) return true;
        if (input >= 'A' && input <= 'Z')
        {
            return (char)(input | 0x20) == lowercaseTarget;
        }
        return false;
    }

    public static ToolCall? ParseToolCallJson(string rawJson, int index)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? name = null;
            if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }
            else if (root.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.String)
            {
                name = fnProp.GetString();
            }
            else if (root.TryGetProperty("tool", out var toolProp) && toolProp.ValueKind == JsonValueKind.String)
            {
                name = toolProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(name))
                return null;

            string argumentsStr = "{}";
            if (root.TryGetProperty("arguments", out var argsProp))
            {
                argumentsStr = argsProp.ValueKind == JsonValueKind.String
                    ? argsProp.GetString() ?? "{}"
                    : argsProp.GetRawText();
            }
            else if (root.TryGetProperty("parameters", out var paramsProp))
            {
                argumentsStr = paramsProp.ValueKind == JsonValueKind.String
                    ? paramsProp.GetString() ?? "{}"
                    : paramsProp.GetRawText();
            }

            return new ToolCall
            {
                Index = index,
                Id = "call_" + Guid.NewGuid().ToString("N")[..8],
                Type = "function",
                Function = new FunctionCall
                {
                    Name = name,
                    Arguments = argumentsStr
                }
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<ToolCall> ExtractToolCalls(string fullText)
    {
        var result = new List<ToolCall>();
        if (string.IsNullOrEmpty(fullText))
            return result;

        int searchIdx = 0;
        int toolIdx = 0;
        while (searchIdx < fullText.Length)
        {
            int startTag = fullText.IndexOf(OpenTag, searchIdx, StringComparison.OrdinalIgnoreCase);
            if (startTag == -1) break;

            int contentStart = startTag + OpenTag.Length;
            int endTag = fullText.IndexOf(CloseTag, contentStart, StringComparison.OrdinalIgnoreCase);
            if (endTag == -1)
            {
                var rawJson = fullText[contentStart..].Trim();
                var tc = ParseToolCallJson(rawJson, toolIdx);
                if (tc != null)
                {
                    toolIdx++;
                    result.Add(tc);
                }
                break;
            }

            var jsonSlice = fullText[contentStart..endTag].Trim();
            var call = ParseToolCallJson(jsonSlice, toolIdx);
            if (call != null)
            {
                toolIdx++;
                result.Add(call);
            }

            searchIdx = endTag + CloseTag.Length;
        }

        return result;
    }

    public static string RemoveToolCallTags(string fullText)
    {
        if (string.IsNullOrEmpty(fullText))
            return string.Empty;

        var sb = new StringBuilder(fullText.Length);
        int searchIdx = 0;
        while (searchIdx < fullText.Length)
        {
            int startTag = fullText.IndexOf(OpenTag, searchIdx, StringComparison.OrdinalIgnoreCase);
            if (startTag == -1)
            {
                sb.Append(fullText.AsSpan(searchIdx));
                break;
            }

            sb.Append(fullText.AsSpan(searchIdx, startTag - searchIdx));
            int endTag = fullText.IndexOf(CloseTag, startTag + OpenTag.Length, StringComparison.OrdinalIgnoreCase);
            if (endTag == -1)
            {
                break;
            }
            searchIdx = endTag + CloseTag.Length;
        }

        return sb.ToString().Trim();
    }
}
