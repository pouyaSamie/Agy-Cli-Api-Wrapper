using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgyApi.Configuration;

namespace AgyApi.Infrastructure.Agy;

public static partial class AgyProcessHelpers
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$", RegexOptions.Compiled)]
    private static partial Regex SafeConversationIdRegex();

    public static bool IsValidConversationId(string? conversationId)
    {
        return !string.IsNullOrWhiteSpace(conversationId) &&
               SafeConversationIdRegex().IsMatch(conversationId);
    }

    public static string ResolveExecutablePath(AgyOptions options)
    {
        var exePath = string.IsNullOrWhiteSpace(options.ExecutablePath) ? "agy" : options.ExecutablePath.Trim();
        if (string.Equals(exePath, "agy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exePath, "agy.exe", StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var candidate = Path.Combine(localAppData, "agy", "bin", "agy.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return exePath;
    }

    public static bool IsExecutableAvailable(AgyOptions options)
    {
        var exePath = ResolveExecutablePath(options);
        if (Path.IsPathRooted(exePath) || exePath.Contains(Path.DirectorySeparatorChar) || exePath.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(exePath);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return false;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), exePath + ext);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static ProcessStartInfo BuildCliProcessStartInfo(
        AgyOptions options,
        string model,
        string? effort,
        string? conversationId,
        bool enableAgentic,
        bool streamJsonInput)
    {
        var workspaceDir = Path.GetFullPath(options.WorkspaceDirectory);
        if (!Directory.Exists(workspaceDir))
        {
            Directory.CreateDirectory(workspaceDir);
        }

        var exePath = ResolveExecutablePath(options);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workspaceDir
        };

        if (streamJsonInput)
        {
            psi.ArgumentList.Add("--input-format");
            psi.ArgumentList.Add("stream-json");
        }

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);

        var hasEffortTier = model.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase) ||
                            model.EndsWith("-high", StringComparison.OrdinalIgnoreCase) ||
                            model.EndsWith("-medium", StringComparison.OrdinalIgnoreCase) ||
                            model.EndsWith("-low", StringComparison.OrdinalIgnoreCase) ||
                            model.EndsWith("-max", StringComparison.OrdinalIgnoreCase);

        var isClaude = model.Contains("claude", StringComparison.OrdinalIgnoreCase);

        if (!hasEffortTier && !isClaude && !string.IsNullOrWhiteSpace(effort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(effort.ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            if (!IsValidConversationId(conversationId))
            {
                throw new ArgumentException("Invalid conversation identifier format.", nameof(conversationId));
            }
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(conversationId);
        }

        // Strictly enforce AllowAgenticTools policy at the CLI flag boundary.
        // Only pass --dangerously-skip-permissions when server policy permits agentic mode AND the caller requested it.
        // Otherwise run with --sandbox and omit auto-approval flags.
        if (enableAgentic && options.AllowAgenticTools)
        {
            psi.ArgumentList.Add("--dangerously-skip-permissions");
        }
        else
        {
            psi.ArgumentList.Add("--sandbox");
        }

        psi.ArgumentList.Add("--disable-slash-commands");
        psi.ArgumentList.Add("--print-timeout");
        psi.ArgumentList.Add($"{Math.Max(1, options.PrintTimeoutMinutes)}m0s");

        return psi;
    }
}
