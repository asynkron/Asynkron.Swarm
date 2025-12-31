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
    string sharedFilePath,
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
    public int AgentNumber { get; } = agentNumber;
    public string WorktreePath { get; } = worktreePath;
    public string TodoFile { get; } = todoFile;
    public string SharedFilePath { get; } = sharedFilePath;
    public bool Autopilot { get; } = autopilot;
    public string? BranchName { get; } = branchName;

    protected override Process SpawnProcess()
    {
        var prompt = WorkerPrompt.Build(TodoFile, Name, SharedFilePath, RestartCount, Autopilot, BranchName, LogPath);
        var arguments = Cli.BuildArguments(prompt, GetModel());
        var stdinContent = Cli.UseStdin ? prompt : null;

        return StartProcess(Cli.FileName, arguments, WorktreePath, stdinContent);
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
