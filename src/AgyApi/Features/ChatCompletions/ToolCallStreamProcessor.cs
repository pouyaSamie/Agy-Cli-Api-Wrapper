using System.Text;
using System.Text.Json;

namespace AgyApi.Features.ChatCompletions;

public class ToolCallStreamProcessor
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";

    private readonly StringBuilder _tagBuffer = new();
    private readonly StringBuilder _toolJsonBuffer = new();
    private readonly StringBuilder _fullContent = new();
    private readonly List<ToolCall> _parsedToolCalls = [];

    private bool _insideToolCall;
    private int _toolIndex;

    public IReadOnlyList<ToolCall> ToolCalls => _parsedToolCalls;
    public bool HasToolCalls => _parsedToolCalls.Count > 0;

    public (string ContentDelta, List<ToolCall>? NewToolCalls) Process(string textDelta)
    {
        if (string.IsNullOrEmpty(textDelta))
        {
            return (string.Empty, null);
        }

        var contentOut = new StringBuilder();
        List<ToolCall>? newCalls = null;

        for (int i = 0; i < textDelta.Length; i++)
        {
            char c = textDelta[i];

            if (_insideToolCall)
            {
                _tagBuffer.Append(c);
                if (CloseTag.StartsWith(_tagBuffer.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    if (_tagBuffer.ToString().Equals(CloseTag, StringComparison.OrdinalIgnoreCase))
                    {
                        _insideToolCall = false;
                        _tagBuffer.Clear();

                        var json = _toolJsonBuffer.ToString().Trim();
                        _toolJsonBuffer.Clear();

                        var tc = ParseToolCallJson(json, _toolIndex++);
                        if (tc != null)
                        {
                            _parsedToolCalls.Add(tc);
                            newCalls ??= [];
                            newCalls.Add(tc);
                        }
                    }
                }
                else
                {
                    _toolJsonBuffer.Append(_tagBuffer.ToString());
                    _tagBuffer.Clear();
                }
            }
            else
            {
                _tagBuffer.Append(c);
                if (OpenTag.StartsWith(_tagBuffer.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    if (_tagBuffer.ToString().Equals(OpenTag, StringComparison.OrdinalIgnoreCase))
                    {
                        _insideToolCall = true;
                        _tagBuffer.Clear();
                    }
                }
                else
                {
                    var flushText = _tagBuffer.ToString();
                    contentOut.Append(flushText);
                    _fullContent.Append(flushText);
                    _tagBuffer.Clear();
                }
            }
        }

        return (contentOut.ToString(), newCalls);
    }

    public (string ContentDelta, List<ToolCall>? NewToolCalls) Flush()
    {
        var contentOut = new StringBuilder();
        List<ToolCall>? newCalls = null;

        if (_insideToolCall)
        {
            _insideToolCall = false;
            _toolJsonBuffer.Append(_tagBuffer.ToString());
            _tagBuffer.Clear();

            var json = _toolJsonBuffer.ToString().Trim();
            _toolJsonBuffer.Clear();

            var tc = ParseToolCallJson(json, _toolIndex++);
            if (tc != null)
            {
                _parsedToolCalls.Add(tc);
                newCalls ??= [];
                newCalls.Add(tc);
            }
        }
        else if (_tagBuffer.Length > 0)
        {
            var remaining = _tagBuffer.ToString();
            contentOut.Append(remaining);
            _fullContent.Append(remaining);
            _tagBuffer.Clear();
        }

        return (contentOut.ToString(), newCalls);
    }

    public string GetFullContent() => _fullContent.ToString();

    public static ToolCall? ParseToolCallJson(string rawJson, int index)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            string? name = null;
            if (root.TryGetProperty("name", out var nameProp))
            {
                name = nameProp.GetString();
            }
            else if (root.TryGetProperty("function", out var fnProp))
            {
                name = fnProp.GetString();
            }
            else if (root.TryGetProperty("tool", out var toolProp))
            {
                name = toolProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(name))
                return null;

            string argumentsStr = "{}";
            if (root.TryGetProperty("arguments", out var argsProp))
            {
                if (argsProp.ValueKind == JsonValueKind.String)
                {
                    argumentsStr = argsProp.GetString() ?? "{}";
                }
                else
                {
                    argumentsStr = argsProp.GetRawText();
                }
            }
            else if (root.TryGetProperty("parameters", out var paramsProp))
            {
                if (paramsProp.ValueKind == JsonValueKind.String)
                {
                    argumentsStr = paramsProp.GetString() ?? "{}";
                }
                else
                {
                    argumentsStr = paramsProp.GetRawText();
                }
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
                var rawJson = fullText.Substring(contentStart).Trim();
                var tc = ParseToolCallJson(rawJson, toolIdx++);
                if (tc != null) result.Add(tc);
                break;
            }

            var jsonSlice = fullText.Substring(contentStart, endTag - contentStart).Trim();
            var call = ParseToolCallJson(jsonSlice, toolIdx++);
            if (call != null) result.Add(call);

            searchIdx = endTag + CloseTag.Length;
        }

        return result;
    }

    public static string RemoveToolCallTags(string fullText)
    {
        if (string.IsNullOrEmpty(fullText))
            return string.Empty;

        var sb = new StringBuilder();
        int searchIdx = 0;
        while (searchIdx < fullText.Length)
        {
            int startTag = fullText.IndexOf(OpenTag, searchIdx, StringComparison.OrdinalIgnoreCase);
            if (startTag == -1)
            {
                sb.Append(fullText.Substring(searchIdx));
                break;
            }

            sb.Append(fullText.Substring(searchIdx, startTag - searchIdx));
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
