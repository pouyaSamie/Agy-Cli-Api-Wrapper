using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using Microsoft.Extensions.Options;

namespace AgyApi.Infrastructure.Auth;

public class ApiKeyAuthMiddleware
{
    private const string AdminCookieName = "agy_admin_key";
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IApiKeyAuthenticator authManager,
        IOptions<AgyOptions> optionsAccessor)
    {
        var path = context.Request.Path;
        var options = optionsAccessor.Value;

        // Public static assets and health probes
        if (path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Admin surfaces: Dashboard (/), Blazor circuit (/_blazor), and Key Management (/api/keys)
        var isAdminSurface = path.Value == "/" ||
                             path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWithSegments("/api/keys", StringComparison.OrdinalIgnoreCase);

        if (isAdminSurface)
        {
            if (IsAuthorizedAdmin(context, options))
            {
                await _next(context);
                return;
            }

            if (IsLoopbackRequest(context) && !HasDisallowedOrigin(context, options))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            var forbiddenObj = new
            {
                error = new
                {
                    message = "Admin UI and key management endpoints require localhost access or a valid Agy:AdminApiKey.",
                    type = "invalid_request_error",
                    param = (string?)null,
                    code = "forbidden_admin_access"
                }
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(forbiddenObj));
            return;
        }

        // OpenAI / API endpoints (/v1/* etc.)
        if (!authManager.HasAnyKeys())
        {
            if (IsLoopbackRequest(context))
            {
                await _next(context);
                return;
            }

            await WriteUnauthorizedAsync(context, "API authentication is required for remote requests. Provision an API key first.");
            return;
        }

        var providedKey = ExtractBearerOrApiKey(context);

        if (IsMatchingSecret(providedKey, options.AdminApiKey))
        {
            context.Items["AuthApiKeyId"] = "admin-key";
            context.Items["AuthApiKeyName"] = "Admin Key";
            await _next(context);
            return;
        }

        var authenticatedKey = authManager.AuthenticateKey(providedKey);
        if (authenticatedKey == null)
        {
            await WriteUnauthorizedAsync(context, "Incorrect or missing API key. Provide a valid Bearer token or api-key header.");
            return;
        }

        context.Items["AuthApiKeyId"] = authenticatedKey.Id;
        context.Items["AuthApiKeyName"] = authenticatedKey.Name;

        await _next(context);
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp == null) return true;
        if (IPAddress.IsLoopback(remoteIp)) return true;
        if (remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4())) return true;
        return false;
    }

    private static bool HasDisallowedOrigin(HttpContext context, AgyOptions options)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            return false;
        }

        var origin = originValues.ToString().Trim();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
        {
            return false;
        }

        if (options.AllowedCorsOrigins != null && options.AllowedCorsOrigins.Length > 0)
        {
            var normalized = origin.TrimEnd('/');
            foreach (var allowed in options.AllowedCorsOrigins)
            {
                if (!string.IsNullOrWhiteSpace(allowed) &&
                    string.Equals(allowed.Trim().TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsAuthorizedAdmin(HttpContext context, AgyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AdminApiKey))
        {
            return false;
        }

        var candidate = ExtractBearerOrApiKey(context);
        if (string.IsNullOrEmpty(candidate) &&
            context.Request.Headers.TryGetValue("X-Admin-Key", out var adminHeader))
        {
            candidate = adminHeader.ToString().Trim();
        }
        if (string.IsNullOrEmpty(candidate) &&
            context.Request.Cookies.TryGetValue(AdminCookieName, out var adminCookie))
        {
            candidate = adminCookie.Trim();
        }

        return IsMatchingSecret(candidate, options.AdminApiKey);
    }

    private static string ExtractBearerOrApiKey(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader["Bearer ".Length..].Trim();
        }

        if (context.Request.Headers.TryGetValue("api-key", out var apiKeyHeader))
        {
            return apiKeyHeader.ToString().Trim();
        }

        return string.Empty;
    }

    internal static bool IsMatchingSecret(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        var errorObj = new
        {
            error = new
            {
                message,
                type = "invalid_request_error",
                param = (string?)null,
                code = "invalid_api_key"
            }
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(errorObj));
    }
}
