using System.Text.Json;
using AgyApi.Features.ChatCompletions;
using AgyApi.Features.Sessions;
using Xunit;

namespace AgyApi.Tests;

public class SessionManagerTests
{
    private readonly ISessionManager _sessionManager;

    public SessionManagerTests()
    {
        _sessionManager = new SessionManager();
    }

    [Fact]
    public void PreparePrompt_WithThinkingEnabled_InjectsThinkTagsDirective()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-high",
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Solve this puzzle" }
            ]
        };

        var prompt = _sessionManager.PreparePrompt(request, isContinuingSession: false, enableThinking: true);

        Assert.Contains("<think>", prompt);
        Assert.Contains("</think>", prompt);
        Assert.Contains("Solve this puzzle", prompt);
    }

    [Fact]
    public void SessionMapping_AssociatesSessionKeyWithConversationId()
    {
        var sessionKey = "user-sess-123";
        var convId = "conv-abc-456";

        _sessionManager.SetConversationId(sessionKey, convId);
        var retrieved = _sessionManager.GetConversationId(sessionKey);

        Assert.Equal(convId, retrieved);
    }

    [Fact]
    public void GetConversationId_DoesNotAcceptArbitraryUnmappedGuid()
    {
        var arbitraryGuid = Guid.NewGuid().ToString("D");
        var retrieved = _sessionManager.GetConversationId(arbitraryGuid);

        Assert.Null(retrieved);
    }

    [Fact]
    public void PreparePrompt_WithTools_InjectsToolsSchemaAndToolCallFormat()
    {
        using var toolsDoc = JsonDocument.Parse(@"[
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""get_weather"",
                    ""description"": ""Get weather for city"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": { ""city"": { ""type"": ""string"" } }
                    }
                }
            }
        ]");

        var request = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash",
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Weather in Tokyo?" }
            ],
            Tools = toolsDoc.RootElement
        };

        var prompt = _sessionManager.PreparePrompt(request, isContinuingSession: false);

        Assert.Contains("<tools>", prompt);
        Assert.Contains("get_weather", prompt);
        Assert.Contains("<tool_call>", prompt);
        Assert.DoesNotContain("DO NOT call or execute tools", prompt);
    }

    [Fact]
    public void PreparePrompt_WithToolResponse_FormatsAndEscapesJsonSafely()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash",
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Weather in Tokyo?" },
                new ChatMessage { Role = "assistant", Content = "<tool_call>{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Tokyo\"}}</tool_call>" },
                new ChatMessage { Role = "tool", Name = "get_\"weather\"", ToolCallId = "call_123", Content = "{\"temp\": 22}" }
            ]
        };

        var prompt = _sessionManager.PreparePrompt(request, isContinuingSession: false);

        Assert.Contains("<tool_response>", prompt);
        // Verify embedded tool response JSON is valid and escaped
        var start = prompt.IndexOf("<tool_response>\n", StringComparison.Ordinal) + "<tool_response>\n".Length;
        var end = prompt.IndexOf("\n</tool_response>", start, StringComparison.Ordinal);
        var jsonBlock = prompt[start..end];
        using var parsed = JsonDocument.Parse(jsonBlock);
        Assert.Equal("get_\"weather\"", parsed.RootElement.GetProperty("name").GetString());
        Assert.Equal(22, parsed.RootElement.GetProperty("content").GetProperty("temp").GetInt32());
    }

    [Fact]
    public void PreparePrompt_PreservesMultipleSystemAndDeveloperMessages()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-medium",
            Messages =
            [
                new ChatMessage { Role = "system", Content = "Rule 1" },
                new ChatMessage { Role = "developer", Content = "Rule 2" },
                new ChatMessage { Role = "user", Content = "Hi" }
            ]
        };

        var prompt = _sessionManager.PreparePrompt(request, isContinuingSession: false);

        Assert.Contains("Rule 1", prompt);
        Assert.Contains("Rule 2", prompt);
    }

    [Fact]
    public void MessageContentConverter_HandlesNullContentOnAssistantToolMessage()
    {
        var json = """{"model":"m","messages":[{"role":"assistant","content":null}]}""";
        var req = JsonSerializer.Deserialize<ChatCompletionRequest>(json);

        Assert.NotNull(req);
        Assert.Single(req!.Messages);
        Assert.Equal(string.Empty, req.Messages[0].Content);
    }
}
