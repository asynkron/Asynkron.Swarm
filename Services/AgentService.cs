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

    public AgentInfo StartWorker(int round, int agentNumber, string worktreePath, string todoFile, string sharedFilePath, AgentType agentType, int restartCount = 0)
    {
        var agentId = $"round{round}-worker{agentNumber}";
        var agentName = $"Worker {agentNumber}";
        var logFilePath = Path.Combine(_logDir, $"{agentId}.log");
        var prompt = WorkerPrompt.Build(todoFile, agentName, sharedFilePath, restartCount);

        // Use gpt-5.2-codex for workers
        var model = agentType == AgentType.Codex ? "gpt-5.2-codex" : null;
        var (fileName, arguments, useStdin) = GetAgentCommand(agentType, prompt, model);
        var process = StartProcess(fileName, arguments, worktreePath, logFilePath, useStdin ? prompt : null, append: restartCount > 0);

        var agent = new AgentInfo
        {
            Id = agentId,
            Name = agentName,
            Kind = AgentKind.Worker,
            Runtime = agentType == AgentType.Claude ? AgentRuntime.Claude : AgentRuntime.Codex,
            LogPath = logFilePath,
            WorktreePath = worktreePath,
            Process = process,
            // Restart context
            Round = round,
            AgentNumber = agentNumber,
            TodoFile = todoFile,
            AgentType = agentType,
            SharedFilePath = sharedFilePath,
            RestartCount = restartCount
        };

        _registry.Register(agent);
        return agent;
    }

    public AgentInfo StartSupervisor(
        int round,
        List<string> worktreePaths,
        List<string> workerLogPaths,
        string repoPath,
        AgentType agentType,
        int restartCount = 0)
    {
        var agentId = $"round{round}-supervisor";
        var agentName = "Supervisor";
        var logFilePath = Path.Combine(_logDir, $"{agentId}.log");
        var prompt = SupervisorPrompt.Build(worktreePaths, workerLogPaths, repoPath, restartCount);

        // Use cheaper model for supervisor (Phase 1 is just observation)
        var model = agentType == AgentType.Codex ? "gpt-5.1-codex-mini" : null;
        var (fileName, arguments, useStdin) = GetAgentCommand(agentType, prompt, model);
        var process = StartProcess(fileName, arguments, repoPath, logFilePath, useStdin ? prompt : null, append: restartCount > 0);

        var agent = new AgentInfo
        {
            Id = agentId,
            Name = agentName,
            Kind = AgentKind.Supervisor,
            Runtime = agentType == AgentType.Claude ? AgentRuntime.Claude : AgentRuntime.Codex,
            LogPath = logFilePath,
            WorktreePath = null,
            Process = process,
            // Restart context
            Round = round,
            AgentType = agentType,
            WorktreePaths = worktreePaths,
            WorkerLogPaths = workerLogPaths,
            RepoPath = repoPath,
            RestartCount = restartCount
        };

        _registry.Register(agent);
        return agent;
    }

    public AgentInfo? RestartAgent(string agentId)
    {
        var agent = _registry.Get(agentId);
        if (agent == null) return null;

        // Kill the current agent
        KillAgent(agent);

        // Append restart marker to log
        var restartMarker = $"\n\n<<agent restarted at {DateTime.Now:O}>>\n\n";
        File.AppendAllText(agent.LogPath, restartMarker);

        // Remove old agent
        _registry.Unregister(agentId);

        // Start new agent with incremented restart count
        var newRestartCount = agent.RestartCount + 1;

        if (agent.Kind == AgentKind.Worker)
        {
            return StartWorker(
                agent.Round,
                agent.AgentNumber,
                agent.WorktreePath!,
                agent.TodoFile!,
                agent.SharedFilePath!,
                agent.AgentType,
                newRestartCount);
        }
        else
        {
            return StartSupervisor(
                agent.Round,
                agent.WorktreePaths!,
                agent.WorkerLogPaths!,
                agent.RepoPath!,
                agent.AgentType,
                newRestartCount);
        }
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

    private static async Task AppendStoppedMarkerAsync(AgentInfo agent)
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

    private static (string FileName, string Arguments, bool UseStdin) GetAgentCommand(AgentType agentType, string prompt, string? model = null)
    {
        var modelArg = model != null ? $"--model {model} " : "";

        return agentType switch
        {
            // Claude needs prompt via stdin in print mode, stream-json for live output
            AgentType.Claude => ("claude", $"-p --dangerously-skip-permissions --tools default --output-format stream-json --verbose {modelArg}".TrimEnd(), true),
            AgentType.Codex => ("codex", $"exec \"{EscapeForShell(prompt)}\" --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox {modelArg}".TrimEnd(), false),
            _ => throw new ArgumentOutOfRangeException(nameof(agentType))
        };
    }

    private static Process StartProcess(string fileName, string arguments, string workingDir, string logFilePath, string? stdinContent = null, bool append = false)
    {
        // Create/truncate or append to log file
        if (append)
        {
            File.AppendAllText(logFilePath, $"[{DateTime.Now:O}] Agent restarted\n");
        }
        else
        {
            File.WriteAllText(logFilePath, $"[{DateTime.Now:O}] Agent started\n");
        }
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
                RedirectStandardInput = stdinContent != null,
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
                        File.AppendAllText(logFilePath, e.Data + Environment.NewLine);
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

        // Write prompt to stdin if needed
        if (stdinContent != null)
        {
            process.StandardInput.Write(stdinContent);
            process.StandardInput.Close();
        }

        return process;
    }

    private static string EscapeForShell(string input)
    {
        // Escape double quotes and backslashes for shell
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
