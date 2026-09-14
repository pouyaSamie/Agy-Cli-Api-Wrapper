namespace AgyApi.Features.ChatCompletions;

public interface IChatCompletionHandler
{
    Task<IResult> HandleAsync(HttpContext context, ChatCompletionRequest request, CancellationToken cancellationToken);
}
