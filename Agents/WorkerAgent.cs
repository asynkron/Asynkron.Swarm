using System.Diagnostics;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.IO;
using Asynkron.Swarm.Prompts;

namespace Asynkron.Swarm.Agents;

public sealed class WorkerAgent(
    int agentNumber,
    string worktreePath,
    string todoFile,
    AgentCliBase cli,
    string logPath,
    int restartCount = 0,
    bool autopilot = false,
    string? branchName = null)
    : AgentBase(id: $"worker{agentNumber}",
        name: $"Worker {agentNumber}",
        cli: cli,
        logPath: logPath,
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

    // Codex models: full name for API, short name for display
    private static readonly (string Model, string Display)[] CodexModels =
    [
        ("gpt-5.2-codex", "5.2-cdx"),
        ("gpt-5.1-codex-max", "5.1-max"),
        ("gpt-5.2", "5.2")
    ];

    // Claude model
    private const string ClaudeModel = "opus";

    public override string? ModelName => GetDisplayModel();

    private string? GetDisplayModel()
    {
        if (Cli is CodexCli)
            return CodexModels[agentNumber % CodexModels.Length].Display;
        if (Cli is ClaudeCli)
            return ClaudeModel;
        return null;
    }

    private string? GetModel()
    {
        if (Cli is CodexCli)
            return CodexModels[agentNumber % CodexModels.Length].Model;
        if (Cli is ClaudeCli)
            return ClaudeModel;
        return null;
    }
}
