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

var port = builder.Configuration.GetValue<int>("Agy:Port", 5200);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// 1. Configuration & Options
builder.Services.Configure<AgyOptions>(builder.Configuration.GetSection(AgyOptions.SectionName));

// 2. Infrastructure Layer
builder.Services.AddSingleton<IAgyTranscriptReader, AgyTranscriptReader>();
builder.Services.AddSingleton<IAgyWorkerPool, AgyWorkerPool>();
builder.Services.AddSingleton<IAgyProcessService, AgyProcessService>();
builder.Services.AddHostedService<AgyBackgroundService>();

// 3. Feature Slices (Adhering to S.O.L.I.D - SRP & ISP)
builder.Services.AddSingleton<ApiKeyManager>();
builder.Services.AddSingleton<IApiKeyManager>(sp => sp.GetRequiredService<ApiKeyManager>());
builder.Services.AddSingleton<IApiKeyAuthenticator>(sp => sp.GetRequiredService<ApiKeyManager>());
builder.Services.AddSingleton<IApiKeyUsageRecorder>(sp => sp.GetRequiredService<ApiKeyManager>());

builder.Services.AddSingleton<IModelRegistryService, ModelRegistryService>();
builder.Services.AddSingleton<ISessionManager, SessionManager>();
builder.Services.AddSingleton<IThoughtStreamProcessor, ThoughtStreamProcessor>();
builder.Services.AddSingleton<IChatCompletionHandler, ChatCompletionHandler>();

// 4. Blazor UI & MudBlazor Components
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// 5. Permissive CORS for Local Tooling & Integrations
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
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
