namespace AgyApi.Features.ChatCompletions;

public static class ChatCompletionEndpoint
{
    public static IEndpointRouteBuilder MapChatSlice(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", async (
            HttpContext context,
            ChatCompletionRequest request,
            IChatCompletionHandler handler,
            CancellationToken ct) =>
        {
            return await handler.HandleAsync(context, request, ct);
        })
        .WithName("CreateChatCompletion")
        .WithTags("Chat");

        return app;
    }
}
