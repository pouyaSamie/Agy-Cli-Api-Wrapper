using System.Net;
using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using AgyApi.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgyApi.Tests;

public class ApiKeyAuthMiddlewareTests
{
    private sealed class StubAuthenticator : IApiKeyAuthenticator
    {
        public bool HasKeys { get; set; }
        public string? ValidKey { get; set; }

        public bool HasAnyKeys() => HasKeys;

        public ApiKeyItem? AuthenticateKey(string? providedKey)
        {
            if (ValidKey != null && providedKey == ValidKey)
            {
                return new ApiKeyItem { Id = "key-1", Name = "Client 1" };
            }
            return null;
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/_blazor")]
    [InlineData("/api/keys")]
    public async Task AdminSurfaces_RemoteWithoutAdminKey_Returns403(string path)
    {
        var called = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        var auth = new StubAuthenticator { HasKeys = true, ValidKey = "agy-live-client" };
        var options = Options.Create(new AgyOptions { AdminApiKey = "super-admin-secret" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        // Even with a valid regular client API key, admin surfaces MUST be forbidden!
        ctx.Request.Headers.Authorization = "Bearer agy-live-client";
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, auth, options);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/_blazor")]
    [InlineData("/api/keys")]
    public async Task AdminSurfaces_RemoteWithValidAdminKey_Allows(string path)
    {
        var called = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        var auth = new StubAuthenticator { HasKeys = true, ValidKey = "agy-live-client" };
        var options = Options.Create(new AgyOptions { AdminApiKey = "super-admin-secret" });

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        ctx.Request.Headers["X-Admin-Key"] = "super-admin-secret";

        await middleware.InvokeAsync(ctx, auth, options);

        Assert.True(called);
    }

    [Fact]
    public async Task AdminSurfaces_LoopbackWithDisallowedOrigin_Returns403()
    {
        var called = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        var auth = new StubAuthenticator { HasKeys = false };
        var options = Options.Create(new AgyOptions());

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/keys";
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Request.Headers.Origin = "https://evil.example";
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, auth, options);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ApiEndpoints_RemoteWithZeroKeys_NeverFailsOpen()
    {
        var called = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        var auth = new StubAuthenticator { HasKeys = false };
        var options = Options.Create(new AgyOptions());

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/v1/models";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, auth, options);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ApiEndpoints_LoopbackWithZeroKeys_AllowsDevAccess()
    {
        var called = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { called = true; return Task.CompletedTask; });
        var auth = new StubAuthenticator { HasKeys = false };
        var options = Options.Create(new AgyOptions());

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/v1/models";
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        await middleware.InvokeAsync(ctx, auth, options);

        Assert.True(called);
    }
}
