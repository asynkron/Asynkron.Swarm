using System.Diagnostics;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.IO;
using Asynkron.Swarm.Prompts;

namespace Asynkron.Swarm.Agents;

public sealed class SupervisorAgent(
    int round,
    List<string> worktreePaths,
    List<string> workerLogPaths,
    string repoPath,
    AgentCliBase cli,
    string logDir,
    int restartCount = 0)
    : AgentBase(id: $"round{round}-supervisor",
        name: "Supervisor",
        cli: cli,
        logPath: Path.Combine(logDir, $"round{round}-supervisor.log"),
        round: round,
        restartCount: restartCount)
{
    private string RepoPath { get; } = repoPath;

    protected override TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(90);

    protected override Process SpawnProcess()
    {
        var prompt = SupervisorPrompt.Build(worktreePaths, workerLogPaths, RepoPath, RestartCount);
        var arguments = Cli.BuildArguments(prompt, GetModel());
        var stdinContent = Cli.UseStdin ? prompt : null;

        return StartProcess(Cli.FileName, arguments, RepoPath, stdinContent);
    }

    protected override void HandleMessage(AgentMessage message)
    {
        // Supervisor only buffers Say messages (reasoning/output)
        // Ignores Do (tool calls) and See (tool results) for cleaner logs
        if (message.Kind == AgentMessageKind.Say)
        {
            AddMessage(message);
        }
    }

    private string? GetModel()
    {
        // Use cheaper model for Codex supervisor
        return Cli is CodexCli ? "gpt-5.1-codex-mini" : null;
    }
}
