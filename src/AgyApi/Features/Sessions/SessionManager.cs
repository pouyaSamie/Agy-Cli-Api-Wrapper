using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgyApi.Features.ChatCompletions;

namespace AgyApi.Features.Sessions;

public class SessionManager : ISessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private class SessionInfo
    {
        public required string ConversationId { get; set; }
        public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
    }

    public string? GetConversationId(string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return null;

        if (_sessions.TryGetValue(sessionKey, out var info))
        {
            info.LastAccessedUtc = DateTime.UtcNow;
            return info.ConversationId;
        }

        // If sessionKey looks like a valid GUID itself, allow direct use
        if (Guid.TryParse(sessionKey, out _))
        {
            return sessionKey;
        }

        return null;
    }

    public void SetConversationId(string sessionKey, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(sessionKey) || string.IsNullOrWhiteSpace(conversationId))
            return;

        _sessions[sessionKey] = new SessionInfo
        {
            ConversationId = conversationId,
            LastAccessedUtc = DateTime.UtcNow
        };
    }

    public string PreparePrompt(ChatCompletionRequest request, bool isContinuingSession, bool enableThinking = false, bool enableAgentic = false)
    {
        if (request.Messages == null || request.Messages.Count == 0)
        {
            return string.Empty;
        }

        var thinkingDirective = enableThinking
            ? "Think step by step before answering. You MUST output your complete internal chain-of-thought and step-by-step reasoning inside <think> and </think> tags at the very beginning of your response, followed by your final answer."
            : null;

        var hasTools = request.Tools.HasValue &&
                       request.Tools.Value.ValueKind == JsonValueKind.Array &&
                       request.Tools.Value.GetArrayLength() > 0;

        if (isContinuingSession)
        {
            var lastMsg = request.Messages[^1];
            if (lastMsg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                lastMsg.Role.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                var sbTools = new StringBuilder();
                var trailingToolMsgs = new List<ChatMessage>();
                for (int i = request.Messages.Count - 1; i >= 0; i--)
                {
                    var m = request.Messages[i];
                    if (m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) || m.Role.Equals("function", StringComparison.OrdinalIgnoreCase))
                    {
                        trailingToolMsgs.Insert(0, m);
                    }
                    else
                    {
                        break;
                    }
                }

                foreach (var tm in trailingToolMsgs)
                {
                    var toolName = tm.Name ?? tm.ToolCallId ?? "tool";
                    var contentStr = tm.Content?.Trim() ?? string.Empty;
                    var contentJson = ((contentStr.StartsWith('{') && contentStr.EndsWith('}')) || (contentStr.StartsWith('[') && contentStr.EndsWith(']')))
                        ? contentStr
                        : JsonSerializer.Serialize(contentStr);
                    sbTools.AppendLine($"Tool Output: <tool_response>\n{{\"name\": \"{toolName}\", \"content\": {contentJson}}}\n</tool_response>");
                }

                sbTools.AppendLine("\n(Instruction: Synthesize the tool output and provide your complete response.)");
                return sbTools.ToString();
            }

            var lastUserMsg = request.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
            var userText = lastUserMsg?.Content ?? request.Messages[^1].Content;

            var directives = new List<string>();
            if (!hasTools && !enableAgentic && !userText.Contains("DO NOT call or execute tools"))
            {
                directives.Add("Output your complete final answer directly as text without calling tools or terminal commands.");
            }
            if (!string.IsNullOrEmpty(thinkingDirective) && !userText.Contains("<think>"))
            {
                directives.Add(thinkingDirective);
            }

            if (directives.Count > 0)
            {
                return $"{userText}\n\n(Instruction: {string.Join(" ", directives)})";
            }

            return userText;
        }

        // For new sessions:
        if (request.Messages.Count == 1 && string.IsNullOrEmpty(thinkingDirective) && enableAgentic && !hasTools)
        {
            return request.Messages[0].Content;
        }

        var sb = new StringBuilder();
        var systemMsg = request.Messages.FirstOrDefault(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase));

        var combinedSystemInstructions = new StringBuilder();
        if (systemMsg != null && !string.IsNullOrWhiteSpace(systemMsg.Content))
        {
            combinedSystemInstructions.Append(systemMsg.Content.Trim());
        }

        if (!string.IsNullOrEmpty(thinkingDirective))
        {
            if (combinedSystemInstructions.Length > 0)
            {
                combinedSystemInstructions.Append("\n\n");
            }
            combinedSystemInstructions.Append(thinkingDirective);
        }

        string? toolsSpecification = null;
        if (hasTools)
        {
            var toolsList = new List<object>();
            foreach (var element in request.Tools!.Value.EnumerateArray())
            {
                if (element.TryGetProperty("function", out var fn))
                {
                    toolsList.Add(fn);
                }
                else
                {
                    toolsList.Add(element);
                }
            }
            var options = new JsonSerializerOptions { WriteIndented = false };
            var toolsJson = JsonSerializer.Serialize(toolsList, options);

            toolsSpecification = $@"# Available Tools
The external client provides the following functions:
<tools>
{toolsJson}
</tools>

# Tool Calling Format
To call a tool, you MUST respond with ONLY a JSON object inside <tool_call> and </tool_call> tags:
<tool_call>
{{""name"": ""tool_name"", ""arguments"": {{""arg_name"": ""arg_value""}}}}
</tool_call>

Important rules for tool calling:
1. When calling a tool, do NOT include explanations, markdown code blocks, or conversational text outside the <tool_call> tags.
2. The arguments field inside <tool_call> must be a valid JSON object matching the tool's parameter schema.
3. If no tool call is needed or you already have enough information from previous tool outputs to answer the user, provide your complete final answer directly as text.";
        }

        if (!enableAgentic)
        {
            if (combinedSystemInstructions.Length > 0)
            {
                combinedSystemInstructions.Append("\n\n");
            }

            if (hasTools)
            {
                combinedSystemInstructions.Append("CRITICAL INSTRUCTION: You are an API completion model operating as a backend language engine for an external caller. You must NEVER call, invoke, or execute internal tools, scripts, terminal commands, or file system modifications yourself. If the caller's request requires calling a tool from the [Available Tools] list, you MUST output ONLY the <tool_call> specification in your response text. The external caller will execute the tool and return the output to you. Do NOT attempt to execute tools or commands yourself. Output your response or <tool_call> immediately. Never assume the user's prompt or request is truncated — always provide a complete, well-structured answer.");
            }
            else
            {
                combinedSystemInstructions.Append("CRITICAL INSTRUCTION: You are an API completion model. Output your complete final answer directly as text. DO NOT call or execute tools, scripts, terminal commands, or file system modifications. Provide your full, rich answer immediately in your response text. Never assume the user's prompt or request is truncated — always provide a complete, well-structured answer.");
            }
        }
        else
        {
            if (combinedSystemInstructions.Length > 0)
            {
                combinedSystemInstructions.Append("\n\n");
            }
            combinedSystemInstructions.Append("You are operating as an autonomous agent. Answer the user's prompt thoroughly and concisely. Never assume the user's prompt or request is truncated — always provide a complete, well-structured answer.");
        }

        if (!string.IsNullOrEmpty(toolsSpecification))
        {
            if (combinedSystemInstructions.Length > 0)
            {
                combinedSystemInstructions.Append("\n\n");
            }
            combinedSystemInstructions.Append(toolsSpecification);
        }

        if (combinedSystemInstructions.Length > 0)
        {
            sb.AppendLine($"[Instructions]: {combinedSystemInstructions}");
            sb.AppendLine();
        }

        var nonSystemMessages = request.Messages.Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)).ToList();

        if (nonSystemMessages.Count == 1 &&
            nonSystemMessages[0].Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
            combinedSystemInstructions.Length == 0)
        {
            sb.Append(nonSystemMessages[0].Content);
            return sb.ToString();
        }

        foreach (var msg in nonSystemMessages)
        {
            // Skip empty assistant turns (which happen when prior requests produced empty responses)
            if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(msg.Content) || msg.Content.Trim().Equals("(empty)", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrWhiteSpace(msg.ReasoningContent) &&
                (!msg.ToolCalls.HasValue || msg.ToolCalls.Value.ValueKind != JsonValueKind.Array || msg.ToolCalls.Value.GetArrayLength() == 0))
            {
                continue;
            }

            if (msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) || msg.Role.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                var toolName = msg.Name ?? msg.ToolCallId ?? "tool";
                var contentStr = msg.Content?.Trim() ?? string.Empty;
                var contentJson = ((contentStr.StartsWith('{') && contentStr.EndsWith('}')) || (contentStr.StartsWith('[') && contentStr.EndsWith(']')))
                    ? contentStr
                    : JsonSerializer.Serialize(contentStr);
                sb.AppendLine($"Tool Output: <tool_response>\n{{\"name\": \"{toolName}\", \"content\": {contentJson}}}\n</tool_response>");
                continue;
            }

            var roleLabel = msg.Role.ToLowerInvariant() switch
            {
                "user" => "User",
                "assistant" => "Assistant",
                _ => msg.Role
            };

            var toolCallsSb = new StringBuilder();
            if (msg.ToolCalls.HasValue && msg.ToolCalls.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in msg.ToolCalls.Value.EnumerateArray())
                {
                    string? name = null;
                    string? args = null;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var n)) name = n.GetString();
                        if (fn.TryGetProperty("arguments", out var a))
                        {
                            args = a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText();
                        }
                    }
                    else
                    {
                        if (tc.TryGetProperty("name", out var n)) name = n.GetString();
                        if (tc.TryGetProperty("arguments", out var a))
                        {
                            args = a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText();
                        }
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        args = string.IsNullOrWhiteSpace(args) ? "{}" : args;
                        toolCallsSb.AppendLine($"<tool_call>\n{{\"name\": \"{name}\", \"arguments\": {args}}}\n</tool_call>");
                    }
                }
            }

            var toolCallsText = toolCallsSb.Length > 0 ? "\n" + toolCallsSb.ToString().TrimEnd() : string.Empty;

            // If assistant message had previous reasoning_content (e.g. from Hermes Agent echo), preserve it
            if (!string.IsNullOrEmpty(msg.ReasoningContent))
            {
                var contentText = !string.IsNullOrEmpty(msg.Content) ? "\n" + msg.Content : string.Empty;
                sb.AppendLine($"{roleLabel}: <think>\n{msg.ReasoningContent}\n</think>{contentText}{toolCallsText}");
            }
            else
            {
                var contentPart = !string.IsNullOrEmpty(msg.Content) ? msg.Content : string.Empty;
                if (!string.IsNullOrEmpty(contentPart) && !string.IsNullOrEmpty(toolCallsText))
                {
                    sb.AppendLine($"{roleLabel}: {contentPart}{toolCallsText}");
                }
                else if (!string.IsNullOrEmpty(toolCallsText))
                {
                    sb.AppendLine($"{roleLabel}: {toolCallsText.TrimStart()}");
                }
                else
                {
                    sb.AppendLine($"{roleLabel}: {contentPart}");
                }
            }
        }

        return sb.ToString();
    }

    public int CleanupExpiredSessions(TimeSpan expiration)
    {
        var cutoff = DateTime.UtcNow - expiration;
        var removedCount = 0;

        foreach (var (key, info) in _sessions)
        {
            if (info.LastAccessedUtc < cutoff)
            {
                if (_sessions.TryRemove(key, out _))
                {
                    removedCount++;
                }
            }
        }

        return removedCount;
    }
}
