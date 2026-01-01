using Asynkron.Swarm.Agents;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.Models;

namespace Asynkron.Swarm.Services;

public class AgentService(AgentRegistry registry, SwarmSession session)
{
    public SwarmSession Session => session;

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
        int agentNumber,
        string todoFile,
        AgentType agentType,
        bool autopilot = false,
        string? branchName = null)
    {
        var cli = CreateCli(agentType);
        var worker = new WorkerAgent(
            agentNumber,
            session.GetWorktreePath(agentNumber),
            todoFile,
            cli,
            session.GetWorkerLogPath(agentNumber),
            autopilot: autopilot,
            branchName: branchName);

        registry.Register(worker);
        return worker;
    }

    public SupervisorAgent CreateSupervisor(
        List<string> worktreePaths,
        List<string> workerLogPaths,
        string repoPath,
        AgentType agentType,
        bool autopilot = false)
    {
        var cli = CreateCli(agentType);
        var supervisor = new SupervisorAgent(
            worktreePaths,
            workerLogPaths,
            repoPath,
            cli,
            session.GetSupervisorLogPath(),
            autopilot: autopilot);

        registry.Register(supervisor);
        return supervisor;
    }

    public void RemoveAgent(AgentBase agent)
    {
        registry.Unregister(agent.Id);
    }

    public void RemoveAllAgents()
    {
        registry.Clear();
    }

    public async Task StopAllWorkersAsync()
    {
        var workers = registry.GetWorkers();

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
