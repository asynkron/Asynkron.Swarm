using System.Diagnostics;
using System.Text.Json;
using Asynkron.Swarm.Models;

namespace Asynkron.Swarm.Services;

public record AgentStatus(AgentType Type, string Executable, bool Installed, bool Responsive, string? Version, string? Error);

public static class AgentDetector
{
    private static readonly Dictionary<AgentType, string> Executables = new()
    {
        [AgentType.Claude] = "claude",
        [AgentType.Codex] = "codex",
        [AgentType.Copilot] = "copilot",
        [AgentType.Gemini] = "gemini"
    };

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".swarm");

    private static readonly string CacheFile = Path.Combine(CacheDir, "agent-status.json");
    private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(30);

    private record CachedStatus(List<AgentStatus> Statuses, DateTime CachedAt);

    public static async Task<List<AgentStatus>> DetectAllAsync(bool fullTest = false, CancellationToken ct = default)
    {
        // Try to load from cache first (only for full tests)
        if (fullTest)
        {
            var cached = await LoadCacheAsync(ct);
            if (cached != null)
            {
                return cached;
            }
        }

        // Run detection
        var tasks = Enum.GetValues<AgentType>()
            .Select(type => DetectAsync(type, fullTest, ct));

        var results = (await Task.WhenAll(tasks)).ToList();

        // Save to cache only if full test
        if (fullTest)
        {
            await SaveCacheAsync(results, ct);
        }

        return results;
    }

    public static async Task<List<AgentStatus>> DetectAllWithCacheAsync(CancellationToken ct = default)
    {
        // Try cache first
        var cached = await LoadCacheAsync(ct);
        if (cached != null)
        {
            return cached;
        }

        // Fall back to quick check (no test prompts)
        return await DetectAllAsync(fullTest: false, ct);
    }

    private static async Task<List<AgentStatus>?> LoadCacheAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(CacheFile))
                return null;

            var json = await File.ReadAllTextAsync(CacheFile, ct);
            var cached = JsonSerializer.Deserialize<CachedStatus>(json);

            if (cached == null)
                return null;

            // Check if cache is still valid
            if (DateTime.UtcNow - cached.CachedAt > CacheDuration)
                return null;

            return cached.Statuses;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveCacheAsync(List<AgentStatus> statuses, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);

            var cached = new CachedStatus(statuses, DateTime.UtcNow);
            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(CacheFile, json, ct);
        }
        catch
        {
            // Ignore cache write errors
        }
    }

    public static async Task<AgentStatus> DetectAsync(AgentType type, bool fullTest = false, CancellationToken ct = default)
    {
        var executable = Executables[type];

        // Check if executable exists
        var (installed, path) = await CheckInstalledAsync(executable, ct);
        if (!installed)
        {
            return new AgentStatus(type, executable, false, false, null, "Not found in PATH");
        }

        // Try to get version
        var (hasVersion, version) = await GetVersionAsync(type, executable, ct);

        // Only run test prompt if fullTest requested
        if (fullTest)
        {
            var (responsive, error) = await TestPromptAsync(type, executable, ct);
            return new AgentStatus(type, executable, true, responsive, version, error);
        }

        // Quick check: assume responsive if installed
        return new AgentStatus(type, executable, true, true, version, null);
    }

    private static async Task<(bool Found, string? Path)> CheckInstalledAsync(string executable, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, null);

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return (true, output.Trim());
            }
            return (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task<(bool HasVersion, string? Version)> GetVersionAsync(AgentType type, string executable, CancellationToken ct)
    {
        try
        {
            var args = type switch
            {
                AgentType.Claude => "--version",
                AgentType.Codex => "--version",
                AgentType.Copilot => "--version",
                AgentType.Gemini => "--version",
                _ => "--version"
            };

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, null);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var result = !string.IsNullOrWhiteSpace(output) ? output.Trim() : error.Trim();
            // Take first line only
            var firstLine = result.Split('\n')[0].Trim();

            return !string.IsNullOrWhiteSpace(firstLine) ? (true, firstLine) : (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task<(bool Responsive, string? Error)> TestPromptAsync(AgentType type, string executable, CancellationToken ct)
    {
        try
        {
            var testPrompt = "Reply with exactly: OK";

            var (args, useStdin) = type switch
            {
                AgentType.Claude => ("-p --dangerously-skip-permissions --max-turns 1", true),
                AgentType.Codex => ($"exec \"{testPrompt}\" --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox", false),
                AgentType.Copilot => ($"-p \"{testPrompt}\" --stream off --model gpt-5", false),
                AgentType.Gemini => ($"\"{testPrompt}\" --yolo", false),
                _ => ("", false)
            };

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = useStdin,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, "Failed to start process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            if (useStdin)
            {
                await process.StandardInput.WriteLineAsync(testPrompt);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }

            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            // Check if we got any meaningful response
            var combined = output + error;
            if (combined.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("ok", StringComparison.Ordinal) ||
                output.Length > 10)  // Got some response
            {
                return (true, null);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return (false, error.Split('\n')[0].Trim());
            }

            return (false, "No response received");
        }
        catch (OperationCanceledException)
        {
            return (false, "Timeout after 30s");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
