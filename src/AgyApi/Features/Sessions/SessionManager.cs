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
                    sbTools.AppendLine(FormatToolResponse(toolName, tm.Content));
                }

                sbTools.AppendLine("\n(Instruction: Synthesize the tool output and provide your complete response.)");
                return sbTools.ToString();
            }

            var lastUserMsg = request.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
            var userText = lastUserMsg?.Content ?? request.Messages[^1].Content ?? string.Empty;

            var directives = new List<string>();
            if (!hasTools && !enableAgentic && !userText.Contains("DO NOT call or execute tools", StringComparison.Ordinal))
            {
                directives.Add("Output your complete final answer directly as text without calling tools or terminal commands.");
            }
            if (!string.IsNullOrEmpty(thinkingDirective) && !userText.Contains("<think>", StringComparison.OrdinalIgnoreCase))
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
            return request.Messages[0].Content ?? string.Empty;
        }

        var sb = new StringBuilder();
        var combinedSystemInstructions = new StringBuilder();

        foreach (var sysMsg in request.Messages.Where(m =>
                     m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                     m.Role.Equals("developer", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(sysMsg.Content))
            {
                if (combinedSystemInstructions.Length > 0)
                {
                    combinedSystemInstructions.Append("\n\n");
                }
                combinedSystemInstructions.Append(sysMsg.Content.Trim());
            }
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
            var toolsJson = JsonSerializer.Serialize(toolsList);

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

        var nonSystemMessages = request.Messages
            .Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                        !m.Role.Equals("developer", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonSystemMessages.Count == 1 &&
            nonSystemMessages[0].Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
            combinedSystemInstructions.Length == 0)
        {
            sb.Append(nonSystemMessages[0].Content ?? string.Empty);
            return sb.ToString();
        }

        foreach (var msg in nonSystemMessages)
        {
            var msgContent = msg.Content ?? string.Empty;

            if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(msgContent) || msgContent.Trim().Equals("(empty)", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrWhiteSpace(msg.ReasoningContent) &&
                (!msg.ToolCalls.HasValue || msg.ToolCalls.Value.ValueKind != JsonValueKind.Array || msg.ToolCalls.Value.GetArrayLength() == 0))
            {
                continue;
            }

            if (msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) || msg.Role.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                var toolName = msg.Name ?? msg.ToolCallId ?? "tool";
                sb.AppendLine(FormatToolResponse(toolName, msgContent));
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
                        toolCallsSb.AppendLine(FormatToolCall(name, args));
                    }
                }
            }

            var toolCallsText = toolCallsSb.Length > 0 ? "\n" + toolCallsSb.ToString().TrimEnd() : string.Empty;

            if (!string.IsNullOrEmpty(msg.ReasoningContent))
            {
                var contentText = !string.IsNullOrEmpty(msgContent) ? "\n" + msgContent : string.Empty;
                sb.AppendLine($"{roleLabel}: <think>\n{msg.ReasoningContent}\n</think>{contentText}{toolCallsText}");
            }
            else
            {
                if (!string.IsNullOrEmpty(msgContent) && !string.IsNullOrEmpty(toolCallsText))
                {
                    sb.AppendLine($"{roleLabel}: {msgContent}{toolCallsText}");
                }
                else if (!string.IsNullOrEmpty(toolCallsText))
                {
                    sb.AppendLine($"{roleLabel}: {toolCallsText.TrimStart()}");
                }
                else
                {
                    sb.AppendLine($"{roleLabel}: {msgContent}");
                }
            }
        }

        return sb.ToString();
    }

    private static string FormatToolResponse(string toolName, string? rawContent)
    {
        var contentStr = rawContent?.Trim() ?? string.Empty;
        object contentObj = contentStr;

        if ((contentStr.StartsWith('{') && contentStr.EndsWith('}')) ||
            (contentStr.StartsWith('[') && contentStr.EndsWith(']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(contentStr);
                contentObj = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Keep as string when content is not valid JSON
            }
        }

        var payload = JsonSerializer.Serialize(new { name = toolName, content = contentObj });
        return $"Tool Output: <tool_response>\n{payload}\n</tool_response>";
    }

    private static string FormatToolCall(string name, string? rawArgs)
    {
        object argsObj;
        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            argsObj = new Dictionary<string, object>();
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                argsObj = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                argsObj = rawArgs;
            }
        }

        var payload = JsonSerializer.Serialize(new { name, arguments = argsObj });
        return $"<tool_call>\n{payload}\n</tool_call>";
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
