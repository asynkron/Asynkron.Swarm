using System.Diagnostics;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Prompts;

namespace Asynkron.Swarm.Services;

public class AgentService
{
    private readonly AgentRegistry _registry;
    private readonly string _logDir;

    public AgentService(AgentRegistry registry)
    {
        _registry = registry;
        _logDir = Path.Combine(Path.GetTempPath(), "swarm-logs");
        Directory.CreateDirectory(_logDir);
    }

    public AgentInfo StartWorker(int round, int agentNumber, string worktreePath, string todoFile, AgentType agentType)
    {
        var agentId = $"round{round}-worker{agentNumber}";
        var agentName = $"Worker {agentNumber}";
        var logFilePath = Path.Combine(_logDir, $"{agentId}.log");
        var prompt = WorkerPrompt.Build(todoFile);

        var (fileName, arguments) = GetAgentCommand(agentType, prompt);
        var process = StartProcess(fileName, arguments, worktreePath, logFilePath);

        var agent = new AgentInfo
        {
            Id = agentId,
            Name = agentName,
            Kind = AgentKind.Worker,
            Runtime = agentType == AgentType.Claude ? AgentRuntime.Claude : AgentRuntime.Codex,
            LogPath = logFilePath,
            WorktreePath = worktreePath,
            Process = process
        };

        _registry.Register(agent);
        return agent;
    }

    public AgentInfo StartSupervisor(
        int round,
        List<string> worktreePaths,
        List<string> workerLogPaths,
        string repoPath,
        AgentType agentType)
    {
        var agentId = $"round{round}-supervisor";
        var agentName = "Supervisor";
        var logFilePath = Path.Combine(_logDir, $"{agentId}.log");
        var prompt = SupervisorPrompt.Build(worktreePaths, workerLogPaths, repoPath);

        var (fileName, arguments) = GetAgentCommand(agentType, prompt);
        var process = StartProcess(fileName, arguments, repoPath, logFilePath);

        var agent = new AgentInfo
        {
            Id = agentId,
            Name = agentName,
            Kind = AgentKind.Supervisor,
            Runtime = agentType == AgentType.Claude ? AgentRuntime.Claude : AgentRuntime.Codex,
            LogPath = logFilePath,
            WorktreePath = null,
            Process = process
        };

        _registry.Register(agent);
        return agent;
    }

    public void KillAgent(AgentInfo agent)
    {
        try
        {
            if (!agent.Process.HasExited)
            {
                agent.Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }

        _registry.MarkStopped(agent.Id);
    }

    public async Task AppendStoppedMarkerAsync(AgentInfo agent)
    {
        const string marker = "\n\n<<worker has been stopped>>\n";
        await File.AppendAllTextAsync(agent.LogPath, marker);
    }

    public async Task KillAllWorkersAsync()
    {
        var workers = _registry.GetByKind(AgentKind.Worker);

        foreach (var worker in workers)
        {
            KillAgent(worker);
            await AppendStoppedMarkerAsync(worker);
        }
    }

    public void RemoveAgent(AgentInfo agent)
    {
        _registry.Unregister(agent.Id);
    }

    public void RemoveAllAgents()
    {
        _registry.Clear();
    }

    private static (string FileName, string Arguments) GetAgentCommand(AgentType agentType, string prompt)
    {
        return agentType switch
        {
            AgentType.Claude => ("claude", $"--print \"{EscapeForShell(prompt)}\""),
            AgentType.Codex => ("codex", $"exec \"{EscapeForShell(prompt)}\" --skip-git-repo-check"),
            _ => throw new ArgumentOutOfRangeException(nameof(agentType))
        };
    }

    private Process StartProcess(string fileName, string arguments, string workingDir, string logFilePath)
    {
        // Create or truncate log file
        File.WriteAllText(logFilePath, $"[{DateTime.Now:O}] Agent started\n");
        File.AppendAllText(logFilePath, $"[{DateTime.Now:O}] Working directory: {workingDir}\n");
        File.AppendAllText(logFilePath, $"[{DateTime.Now:O}] Command: {fileName} {arguments}\n");
        File.AppendAllText(logFilePath, new string('-', 60) + "\n\n");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var logLock = new object();

        // Redirect output to log file
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (logLock)
                {
                    try
                    {
                        File.AppendAllText(logFilePath, e.Data + Environment.NewLine);
                    }
                    catch
                    {
                        // Ignore write errors
                    }
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (logLock)
                {
                    try
                    {
                        File.AppendAllText(logFilePath, $"[ERROR] {e.Data}" + Environment.NewLine);
                    }
                    catch
                    {
                        // Ignore write errors
                    }
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static string EscapeForShell(string input)
    {
        // Escape double quotes and backslashes for shell
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
