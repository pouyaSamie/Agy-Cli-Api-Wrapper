using AgyApi.Features.ChatCompletions;

namespace AgyApi.Features.Sessions;

public interface ISessionManager
{
    string? GetConversationId(string sessionKey);
    void SetConversationId(string sessionKey, string conversationId);
    string PreparePrompt(ChatCompletionRequest request, bool isContinuingSession, bool enableThinking = false, bool enableAgentic = false);
    int CleanupExpiredSessions(TimeSpan expiration);
}
