namespace AgyApi.Configuration;

public class AgyOptions
{
    public const string SectionName = "Agy";

    public int Port { get; set; } = 5200;
    public string ExecutablePath { get; set; } = "agy";
    public string DefaultModel { get; set; } = "gemini-3.8-flash-high";
    public string WorkspaceDirectory { get; set; } = "./workspace";
    public bool AllowAgenticTools { get; set; } = false;
    public bool IncludeReasoningContent { get; set; } = true;
    public string ReasoningFormat { get; set; } = "both"; // "both", "field", "tags"
    public string DefaultReasoningEffort { get; set; } = "high"; // "low", "medium", "high"
    public bool EnableThinkingDirectives { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = "data/agy_gateway.db";

    // Worker Pool Settings
    public bool EnableWorkerPool { get; set; } = true;
    public int MaxPoolSize { get; set; } = 3;
    public int WorkerIdleTimeoutMinutes { get; set; } = 10;
    public int MaxTurnsPerWorker { get; set; } = 50;
    public int WarmWorkersOnStartup { get; set; } = 1;

    // Timeout & Keep-Alive Settings
    public int PrintTimeoutMinutes { get; set; } = 30;
    public int StreamKeepAliveSeconds { get; set; } = 15;
}
