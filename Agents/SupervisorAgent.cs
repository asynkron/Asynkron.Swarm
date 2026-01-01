using System.Diagnostics;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.IO;
using Asynkron.Swarm.Prompts;

namespace Asynkron.Swarm.Agents;

public sealed class SupervisorAgent(
    List<string> worktreePaths,
    List<string> workerLogPaths,
    string repoPath,
    AgentCliBase cli,
    string logPath,
    int restartCount = 0,
    bool autopilot = false)
    : AgentBase(id: "supervisor",
        name: "Supervisor",
        cli: cli,
        logPath: logPath,
        restartCount: restartCount)
{

    protected override Process SpawnProcess()
    {
        var prompt = autopilot
            ? SupervisorPrompt.BuildAutopilot(worktreePaths, workerLogPaths, RestartCount)
            : SupervisorPrompt.Build(worktreePaths, workerLogPaths, repoPath, RestartCount);
        var arguments = Cli.BuildArguments(prompt, GetModel());
        var stdinContent = Cli.UseStdin ? prompt : null;

        return StartProcess(Cli.FileName, arguments, repoPath, stdinContent);
    }

    protected override void HandleMessage(AgentMessage message)
    {
        if (message.Kind == AgentMessageKind.Say)
        {
            AddMessage(message);
        }
        else if (message.Kind == AgentMessageKind.Do)
        {
            // Parse tool calls to show meaningful activity
            var summary = GetToolActivitySummary(message);
            if (summary != null)
            {
                AddMessage(new AgentMessage(AgentMessageKind.Do, summary));
            }
        }
    }

    private string? GetToolActivitySummary(AgentMessage message)
    {
        var content = message.Content;
        var toolInput = message.ToolInput ?? "";

        // Check if accessing a worker's log file
        for (var i = 0; i < workerLogPaths.Count; i++)
        {
            if (content.Contains(workerLogPaths[i]) || toolInput.Contains(workerLogPaths[i]))
            {
                return $"Reading logs for Worker {i + 1}";
            }
        }

        // Check if accessing a worker's worktree
        for (var i = 0; i < worktreePaths.Count; i++)
        {
            if (!content.Contains(worktreePaths[i]) && !toolInput.Contains(worktreePaths[i])) continue;
            var action = message.ToolName switch
            {
                "Bash" when content.Contains("git status") => "Checking git status",
                "Bash" when content.Contains("git diff") => "Checking git diff",
                "Bash" when content.Contains("git log") => "Checking git log",
                "Bash" when content.Contains("git cherry-pick") => "Cherry-picking commits",
                "Bash" when content.Contains("git merge") => "Merging changes",
                "Read" => "Reading file",
                "Glob" => "Searching files",
                "Grep" => "Searching code",
                _ => "Inspecting"
            };
            return $"{action} for Worker {i + 1}";
        }

        return null;
    }

    private const string ClaudeModel = "sonnet";
    private const string CodexModel = "gpt-5.1-codex-mini";

    public override string? ModelName => Cli switch
    {
        ClaudeCli => ClaudeModel,
        CodexCli => "5.1-mini",
        _ => null
    };

    private string? GetModel() => Cli switch
    {
        ClaudeCli => ClaudeModel,
        CodexCli => CodexModel,
        _ => null
    };
}
