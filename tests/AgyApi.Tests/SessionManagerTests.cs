using AgyApi.Configuration;
using AgyApi.Features.ChatCompletions;
using AgyApi.Features.Sessions;
using Microsoft.Extensions.Options;
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
    public void PreparePrompt_WithTools_InjectsToolsSchemaAndToolCallFormat()
    {
        using var toolsDoc = System.Text.Json.JsonDocument.Parse(@"[
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
    public void PreparePrompt_WithToolResponse_FormatsToolResponseBlock()
    {
        var request = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash",
            Messages =
            [
                new ChatMessage { Role = "user", Content = "Weather in Tokyo?" },
                new ChatMessage { Role = "assistant", Content = "<tool_call>{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Tokyo\"}}</tool_call>" },
                new ChatMessage { Role = "tool", Name = "get_weather", ToolCallId = "call_123", Content = "{\"temp\": 22}" }
            ]
        };

        var prompt = _sessionManager.PreparePrompt(request, isContinuingSession: false);

        Assert.Contains("<tool_response>", prompt);
        Assert.Contains("get_weather", prompt);
        Assert.Contains("{\"temp\": 22}", prompt);
    }
}
