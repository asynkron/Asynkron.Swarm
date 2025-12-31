using System.ComponentModel;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Asynkron.Swarm.Commands;

public class SwarmSettings : CommandSettings
{
    private static readonly string DefaultRepo = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "git", "asynkron", "Asynkron.JsEngine");

    [CommandOption("-a|--agents <COUNT>")]
    [Description("Number of worker agents to spawn")]
    [DefaultValue(4)]
    public int Agents { get; init; } = 4;

    [CommandOption("-r|--repo <PATH>")]
    [Description("Path to the git repository")]
    public string Repo { get; init; } = DefaultRepo;

    [CommandOption("-t|--todo <FILE>")]
    [Description("Name of the todo file (relative to repo root)")]
    [DefaultValue("todo.md")]
    public string Todo { get; init; } = "todo.md";

    [CommandOption("-m|--minutes <MINUTES>")]
    [Description("Minutes to run each round before killing workers")]
    [DefaultValue(5)]
    public int Minutes { get; init; } = 5;

    [CommandOption("--agent-type <TYPE>")]
    [Description("Agent CLI to use: Claude or Codex")]
    [DefaultValue(AgentType.Claude)]
    public AgentType AgentType { get; init; } = AgentType.Claude;

    [CommandOption("--max-rounds <COUNT>")]
    [Description("Maximum number of rounds before stopping")]
    [DefaultValue(10)]
    public int MaxRounds { get; init; } = 10;

    public override ValidationResult Validate()
    {
        if (Agents < 1)
        {
            return ValidationResult.Error("Number of agents must be at least 1");
        }

        if (Minutes < 1)
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

public class SwarmCommand : AsyncCommand<SwarmSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SwarmSettings settings)
    {
        try
        {
            var options = new SwarmOptions
            {
                Agents = settings.Agents,
                Repo = settings.Repo,
                Todo = settings.Todo,
                Minutes = settings.Minutes,
                AgentType = settings.AgentType,
                MaxRounds = settings.MaxRounds
            };

            var orchestrator = new RoundOrchestrator();
            await orchestrator.RunAsync(options);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            return 1;
        }
    }
}
