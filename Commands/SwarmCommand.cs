using System.ComponentModel;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Services;
using JetBrains.Annotations;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Asynkron.Swarm.Commands;

[UsedImplicitly]
public class SwarmSettings : CommandSettings
{
    private static readonly string DefaultRepo = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "git", "asynkron", "Asynkron.JsEngine");

    [CommandOption("--claude <COUNT>")]
    [Description("Number of Claude worker agents (default: 4 if no agents specified)")]
    [DefaultValue(0)]
    [UsedImplicitly]
    public int ClaudeWorkers { get; init; } = 0;

    [CommandOption("--codex <COUNT>")]
    [Description("Number of Codex worker agents")]
    [DefaultValue(0)]
    [UsedImplicitly]
    public int CodexWorkers { get; init; } = 0;

    [CommandOption("--copilot <COUNT>")]
    [Description("Number of Copilot worker agents")]
    [DefaultValue(0)]
    [UsedImplicitly]
    public int CopilotWorkers { get; init; } = 0;

    [CommandOption("--gemini <COUNT>")]
    [Description("Number of Gemini worker agents")]
    [DefaultValue(0)]
    [UsedImplicitly]
    public int GeminiWorkers { get; init; } = 0;

    [CommandOption("--each <COUNT>")]
    [Description("Spawn this many of each agent type")]
    [DefaultValue(0)]
    [UsedImplicitly]
    public int Each { get; init; } = 0;

    [CommandOption("-r|--repo <PATH>")]
    [Description("Path to the git repository")]
    [UsedImplicitly]
    public string Repo { get; init; } = DefaultRepo;

    [CommandOption("-t|--todo <FILE>")]
    [Description("Name of the todo file (relative to repo root)")]
    [DefaultValue("todo/todo.md")]
    [UsedImplicitly]
    public string Todo { get; init; } = "todo/todo.md";

    [CommandOption("-m|--minutes <MINUTES>")]
    [Description("Minutes to run each round before killing workers")]
    [DefaultValue(15)]
    [UsedImplicitly]
    public int Minutes { get; init; } = 15;

    [CommandOption("--supervisor <TYPE>")]
    [Description("Supervisor agent type: Claude, Codex, Copilot, or Gemini")]
    [DefaultValue(AgentType.Claude)]
    [UsedImplicitly]
    public AgentType SupervisorType { get; init; } = AgentType.Claude;

    [CommandOption("--max-rounds <COUNT>")]
    [Description("Maximum number of rounds before stopping")]
    [DefaultValue(10)]
    [UsedImplicitly]
    public int MaxRounds { get; init; } = 10;

    [CommandOption("--autopilot")]
    [Description("Run without timer, create GitHub PRs when done, then exit")]
    [DefaultValue(false)]
    [UsedImplicitly]
    public bool Autopilot { get; init; } = false;

    public override ValidationResult Validate()
    {
        if (ClaudeWorkers < 0)
        {
            return ValidationResult.Error("Claude workers cannot be negative");
        }

        if (CodexWorkers < 0)
        {
            return ValidationResult.Error("Codex workers cannot be negative");
        }

        if (CopilotWorkers < 0)
        {
            return ValidationResult.Error("Copilot workers cannot be negative");
        }

        if (GeminiWorkers < 0)
        {
            return ValidationResult.Error("Gemini workers cannot be negative");
        }

        if (!Autopilot && Minutes < 1)
        {
            return ValidationResult.Error("Minutes must be at least 1");
        }

        if (MaxRounds < 1)
        {
            return ValidationResult.Error("Max rounds must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(Repo))
        {
            return ValidationResult.Error("Repository path is required");
        }

        var repoPath = Path.GetFullPath(Repo);
        if (!Directory.Exists(repoPath))
        {
            return ValidationResult.Error($"Repository path does not exist: {repoPath}");
        }

        var gitDir = Path.Combine(repoPath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            return ValidationResult.Error($"Not a git repository: {repoPath}");
        }

        var todoPath = Path.Combine(repoPath, Todo);
        if (!File.Exists(todoPath))
        {
            return ValidationResult.Error($"Todo file not found: {todoPath}");
        }

        return ValidationResult.Success();
    }
}

[UsedImplicitly]
public class SwarmCommand : AsyncCommand<SwarmSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SwarmSettings settings)
    {
        var orchestrator = new RoundOrchestrator();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // Prevent immediate termination
            orchestrator.KillAllAgents();
            orchestrator.CleanupWorktreesAsync().GetAwaiter().GetResult();
            Environment.Exit(0);
        };

        // Also handle process exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            orchestrator.KillAllAgents();
        };

        try
        {
            // Handle --each flag: spawn N of each agent type
            var claudeWorkers = settings.Each > 0 ? settings.Each : settings.ClaudeWorkers;
            var codexWorkers = settings.Each > 0 ? settings.Each : settings.CodexWorkers;
            var copilotWorkers = settings.Each > 0 ? settings.Each : settings.CopilotWorkers;
            var geminiWorkers = settings.Each > 0 ? settings.Each : settings.GeminiWorkers;

            // Default to 4 Claude workers if no agents specified
            var totalSpecified = claudeWorkers + codexWorkers + copilotWorkers + geminiWorkers;
            if (totalSpecified == 0)
                claudeWorkers = 4;

            var options = new SwarmOptions
            {
                ClaudeWorkers = claudeWorkers,
                CodexWorkers = codexWorkers,
                CopilotWorkers = copilotWorkers,
                GeminiWorkers = geminiWorkers,
                Repo = settings.Repo,
                Todo = settings.Todo,
                Minutes = settings.Minutes,
                SupervisorType = settings.SupervisorType,
                MaxRounds = settings.MaxRounds,
                Autopilot = settings.Autopilot
            };

            await orchestrator.RunAsync(options);

            return 0;
        }
        catch (Exception ex)
        {
            orchestrator.KillAllAgents();
            await orchestrator.CleanupWorktreesAsync();
            // Print error after cleanup (UI is stopped by now)
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }
}
