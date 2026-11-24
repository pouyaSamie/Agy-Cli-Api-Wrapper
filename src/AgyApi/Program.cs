using AgyApi.Components;
using AgyApi.Configuration;
using AgyApi.Features.ApiKeys;
using AgyApi.Features.ChatCompletions;
using AgyApi.Features.Health;
using AgyApi.Features.Models;
using AgyApi.Features.Sessions;
using AgyApi.Infrastructure.Agy;
using AgyApi.Infrastructure.Auth;
using AgyApi.Infrastructure.Background;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? builder.Configuration["urls"];
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    var bindAddress = builder.Configuration.GetValue<string>("Agy:BindAddress");
    if (string.IsNullOrWhiteSpace(bindAddress))
    {
        bindAddress = "127.0.0.1";
    }
    var port = builder.Configuration.GetValue<int>("Agy:Port", 5200);
    builder.WebHost.UseUrls($"http://{bindAddress}:{port}");
}

// 1. Configuration & Options
builder.Services.Configure<AgyOptions>(builder.Configuration.GetSection(AgyOptions.SectionName));

// 2. Infrastructure Layer
builder.Services.AddSingleton<IAgyTranscriptReader, AgyTranscriptReader>();
builder.Services.AddSingleton<IAgyWorkerPool, AgyWorkerPool>();
builder.Services.AddSingleton<IAgyProcessService, AgyProcessService>();
builder.Services.AddHostedService<AgyBackgroundService>();

// 3. Feature Slices
builder.Services.AddSingleton<ApiKeyManager>();
builder.Services.AddSingleton<IApiKeyManager>(sp => sp.GetRequiredService<ApiKeyManager>());
builder.Services.AddSingleton<IApiKeyAuthenticator>(sp => sp.GetRequiredService<ApiKeyManager>());
builder.Services.AddSingleton<IApiKeyUsageRecorder>(sp => sp.GetRequiredService<ApiKeyManager>());

builder.Services.AddSingleton<IModelRegistryService, ModelRegistryService>();
builder.Services.AddSingleton<ISessionManager, SessionManager>();
builder.Services.AddSingleton<IChatCompletionHandler, ChatCompletionHandler>();

// 4. Blazor UI & MudBlazor Components
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// 5. Restricted CORS (Loopback + explicit Agy:AllowedCorsOrigins allowlist)
var allowedCorsOrigins = builder.Configuration.GetSection("Agy:AllowedCorsOrigins").Get<string[]>() ?? [];
var allowedOriginSet = new HashSet<string>(
    allowedCorsOrigins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim().TrimEnd('/')),
    StringComparer.OrdinalIgnoreCase);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            if (string.IsNullOrWhiteSpace(origin)) return false;
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
            {
                return true;
            }
            return allowedOriginSet.Contains(origin.TrimEnd('/'));
        })
        .AllowAnyMethod()
        .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseMiddleware<ApiKeyAuthMiddleware>();

// 6. Map Feature Slices (Vertical Slice Architecture)
app.MapHealthSlice();
app.MapModelSlice();
app.MapApiKeySlice();
app.MapChatSlice();

// 7. Map Blazor Dashboard
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
