using System.Text.Json;
using AgyApi.Features.ApiKeys;

namespace AgyApi.Infrastructure.Auth;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IApiKeyAuthenticator authManager)
    {
        var path = context.Request.Path;

        // Allow public access to Web UI, Blazor hubs, MudBlazor static assets, health check, and key endpoints
        if (path.Value == "/" ||
            path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/keys", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Only enforce auth on API calls (e.g. /v1/*) when at least one key exists
        if (!authManager.HasAnyKeys())
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        var providedKey = string.Empty;

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            providedKey = authHeader["Bearer ".Length..].Trim();
        }
        else if (context.Request.Headers.TryGetValue("api-key", out var apiKeyHeader))
        {
            providedKey = apiKeyHeader.ToString().Trim();
        }

        var authenticatedKey = authManager.AuthenticateKey(providedKey);

        if (authenticatedKey == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";

            var errorObj = new
            {
                error = new
                {
                    message = "Incorrect or missing API key. Obtain a key from the Web UI at http://localhost:5200 or provide a valid Bearer token.",
                    type = "invalid_request_error",
                    param = (string?)null,
                    code = "invalid_api_key"
                }
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(errorObj));
            return;
        }

        // Store authenticated key id in HttpContext for downstream token telemetry recording
        context.Items["AuthApiKeyId"] = authenticatedKey.Id;
        context.Items["AuthApiKeyName"] = authenticatedKey.Name;

        await _next(context);
    }
}
