using Asynkron.Swarm.Agents;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.Models;

namespace Asynkron.Swarm.Services;

public class AgentService
{
    private readonly AgentRegistry _registry;
    private readonly string _logDir;

    public string LogDir => _logDir;

    public AgentService(AgentRegistry registry)
    {
        _registry = registry;
        _logDir = Path.Combine(Path.GetTempPath(), "swarm-logs");
        Directory.CreateDirectory(_logDir);
    }

    public static AgentCliBase CreateCli(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Claude => new ClaudeCli(),
            AgentType.Codex => new CodexCli(),
            AgentType.Copilot => new CopilotCli(),
            AgentType.Gemini => new GeminiCli(),
            _ => throw new ArgumentOutOfRangeException(nameof(agentType))
        };
    }

    public WorkerAgent CreateWorker(
        int round,
        int agentNumber,
        string worktreePath,
        string todoFile,
        string sharedFilePath,
        AgentType agentType,
        bool autopilot = false,
        string? branchName = null)
    {
        var cli = CreateCli(agentType);
        var worker = new WorkerAgent(
            round,
            agentNumber,
            worktreePath,
            todoFile,
            sharedFilePath,
            cli,
            _logDir,
            restartCount: 0,
            autopilot: autopilot,
            branchName: branchName);

        _registry.Register(worker);
        return worker;
    }

    public SupervisorAgent CreateSupervisor(
        int round,
        List<string> worktreePaths,
        List<string> workerLogPaths,
        string repoPath,
        AgentType agentType,
        bool autopilot = false)
    {
        var cli = CreateCli(agentType);
        var supervisor = new SupervisorAgent(
            round,
            worktreePaths,
            workerLogPaths,
            repoPath,
            cli,
            _logDir,
            restartCount: 0,
            autopilot: autopilot);

        _registry.Register(supervisor);
        return supervisor;
    }

    public void RemoveAgent(AgentBase agent)
    {
        _registry.Unregister(agent.Id);
    }

    public void RemoveAllAgents()
    {
        _registry.Clear();
    }

    public async Task StopAllWorkersAsync()
    {
        var workers = _registry.GetWorkers();

        foreach (var worker in workers)
        {
            worker.Stop();
            await AppendStoppedMarkerAsync(worker);
        }
    }

    private static async Task AppendStoppedMarkerAsync(AgentBase agent)
    {
        const string marker = "\n\n<<worker has been stopped>>\n";
        await File.AppendAllTextAsync(agent.LogPath, marker);
    }
}
