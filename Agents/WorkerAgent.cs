using System.Diagnostics;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.IO;
using Asynkron.Swarm.Prompts;

namespace Asynkron.Swarm.Agents;

public sealed class WorkerAgent(
    int round,
    int agentNumber,
    string worktreePath,
    string todoFile,
    AgentCliBase cli,
    string logDir,
    int restartCount = 0,
    bool autopilot = false,
    string? branchName = null)
    : AgentBase(id: $"round{round}-worker{agentNumber}",
        name: $"Worker {agentNumber}",
        cli: cli,
        logPath: Path.Combine(logDir, $"round{round}-worker{agentNumber}.log"),
        round: round,
        restartCount: restartCount)
{


    // Workers should not restart on clean exit (exit code 0)
    protected override bool RestartOnCleanExit => false;

    protected override Process SpawnProcess()
    {
        var prompt = WorkerPrompt.Build(todoFile, Name, RestartCount, autopilot, branchName, LogPath);
        var arguments = Cli.BuildArguments(prompt, GetModel());
        var stdinContent = Cli.UseStdin ? prompt : null;

        return StartProcess(Cli.FileName, arguments, worktreePath, stdinContent);
    }

    protected override void HandleMessage(AgentMessage message)
    {
        // Worker buffers all message types (Say, Do, See)
        AddMessage(message);
    }

    private string? GetModel()
    {
        // Use gpt-5.2-codex for Codex workers
        return Cli is CodexCli ? "gpt-5.2-codex" : null;
    }
}
