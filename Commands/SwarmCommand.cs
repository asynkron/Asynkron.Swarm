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

    [CommandOption("-r|--repo <PATH>")]
    [Description("Path to the git repository (default: current directory if in a repo)")]
    [UsedImplicitly]
    public string? Repo { get; init; }

    [CommandOption("-t|--todo <FILE>")]
    [Description("Name of the todo file (relative to repo root)")]
    [DefaultValue("todo.md")]
    [UsedImplicitly]
    public string Todo { get; init; } = "todo.md";

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

    [CommandOption("--arena")]
    [Description("Arena mode: timed rounds, supervisor evaluates and picks winning changes")]
    [DefaultValue(false)]
    [UsedImplicitly]
    public bool Arena { get; init; } = false;

    [CommandOption("--autopilot")]
    [Description("Autopilot mode: runs continuously without timed rounds (default)")]
    [DefaultValue(true)]
    [UsedImplicitly]
    public bool Autopilot { get; init; } = true;

    [CommandOption("--resume <SESSION_ID>")]
    [Description("Resume a previous session by its ID")]
    [UsedImplicitly]
    public string? Resume { get; init; }

    [CommandOption("--detect")]
    [Description("Detect installed CLI agents and exit")]
    [DefaultValue(false)]
    [UsedImplicitly]
    public bool Detect { get; init; } = false;

    [CommandOption("--skip-detect")]
    [Description("Skip agent detection at startup")]
    [DefaultValue(false)]
    [UsedImplicitly]
    public bool SkipDetect { get; init; } = false;

    // Resolved repo path (explicit or detected from current directory)
    public string? ResolvedRepo { get; private set; }

    public override ValidationResult Validate()
    {
        // Skip all validation if just detecting agents
        if (Detect)
        {
            return ValidationResult.Success();
        }

        // Skip all validation if resuming - session has all the info we need
        if (!string.IsNullOrEmpty(Resume))
        {
            return ValidationResult.Success();
        }

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

        // Resolve repo path
        if (!string.IsNullOrWhiteSpace(Repo))
        {
            ResolvedRepo = Path.GetFullPath(Repo);
        }
        else
        {
            // Try to find git repo from current directory
            ResolvedRepo = FindGitRoot(Environment.CurrentDirectory);
            if (ResolvedRepo == null)
            {
                return ValidationResult.Error("Not in a git repository. Use --repo to specify a path.");
            }
        }

        if (!Directory.Exists(ResolvedRepo))
        {
            return ValidationResult.Error($"Repository path does not exist: {ResolvedRepo}");
        }

        var gitDir = Path.Combine(ResolvedRepo, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            return ValidationResult.Error($"Not a git repository: {ResolvedRepo}");
        }

        var todoPath = Path.Combine(ResolvedRepo, Todo);
        if (!File.Exists(todoPath))
        {
            return ValidationResult.Error($"Todo file not found: {todoPath}");
        }

        return ValidationResult.Success();
    }

    private static string? FindGitRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            var gitDir = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitDir) || File.Exists(gitDir))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}

[UsedImplicitly]
public class SwarmCommand : AsyncCommand<SwarmSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SwarmSettings settings)
    {
        // Handle --detect flag
        if (settings.Detect)
        {
            return await RunDetectionAsync();
        }

        var orchestrator = new RoundOrchestrator();

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // Prevent immediate termination
            orchestrator.KillAllAgents();
            Environment.Exit(0);
        };

        // Also handle process exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            orchestrator.KillAllAgents();
        };

        try
        {
            // Default to 2 Claude workers if no agents specified
            var claudeWorkers = settings.ClaudeWorkers;
            var codexWorkers = settings.CodexWorkers;
            var copilotWorkers = settings.CopilotWorkers;
            var geminiWorkers = settings.GeminiWorkers;

            if (claudeWorkers + codexWorkers + copilotWorkers + geminiWorkers == 0)
                claudeWorkers = 2;

            // Run agent detection unless skipped
            if (!settings.SkipDetect)
            {
                var hasErrors = await ValidateRequestedAgentsAsync(
                    claudeWorkers, codexWorkers, copilotWorkers, geminiWorkers, settings.SupervisorType);
                if (hasErrors)
                {
                    return 1;
                }
            }

            var options = new SwarmOptions
            {
                ClaudeWorkers = claudeWorkers,
                CodexWorkers = codexWorkers,
                CopilotWorkers = copilotWorkers,
                GeminiWorkers = geminiWorkers,
                Repo = settings.ResolvedRepo!,
                Todo = settings.Todo,
                Minutes = settings.Minutes,
                SupervisorType = settings.SupervisorType,
                MaxRounds = settings.MaxRounds,
                Arena = settings.Arena,
                Autopilot = settings.Autopilot
            };

            await orchestrator.RunAsync(options, settings.Resume);

            return 0;
        }
        catch (Exception ex)
        {
            orchestrator.KillAllAgents();
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunDetectionAsync()
    {
        AnsiConsole.MarkupLine("[bold]Detecting CLI agents...[/]\n");

        // Full test with prompts when explicitly running --detect
        var statuses = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing agents (this may use API credits)...", async _ => await AgentDetector.DetectAllAsync(fullTest: true));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Agent")
            .AddColumn("Executable")
            .AddColumn("Installed")
            .AddColumn("Responsive")
            .AddColumn("Version")
            .AddColumn("Notes");

        foreach (var status in statuses)
        {
            var installedMark = status.Installed ? "[green]✓[/]" : "[red]✗[/]";
            var responsiveMark = status.Responsive ? "[green]✓[/]" : (status.Installed ? "[yellow]?[/]" : "[dim]-[/]");
            var version = status.Version ?? "[dim]-[/]";
            var notes = status.Error ?? (status.Responsive ? "[green]Ready[/]" : "[dim]-[/]");

            table.AddRow(
                status.Type.ToString(),
                status.Executable,
                installedMark,
                responsiveMark,
                version,
                notes);
        }

        AnsiConsole.Write(table);

        var readyCount = statuses.Count(s => s.Responsive);
        var installedCount = statuses.Count(s => s.Installed);

        AnsiConsole.WriteLine();
        if (readyCount == statuses.Count)
        {
            AnsiConsole.MarkupLine("[green]All agents ready![/]");
        }
        else if (readyCount > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{readyCount}/{statuses.Count} agents ready[/]");
        }
        else if (installedCount > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{installedCount} agents installed but not responding[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]No agents found![/]");
        }

        return readyCount > 0 ? 0 : 1;
    }

    private static async Task<bool> ValidateRequestedAgentsAsync(
        int claudeWorkers, int codexWorkers, int copilotWorkers, int geminiWorkers, AgentType supervisorType)
    {
        var requiredTypes = new HashSet<AgentType>();

        if (claudeWorkers > 0) requiredTypes.Add(AgentType.Claude);
        if (codexWorkers > 0) requiredTypes.Add(AgentType.Codex);
        if (copilotWorkers > 0) requiredTypes.Add(AgentType.Copilot);
        if (geminiWorkers > 0) requiredTypes.Add(AgentType.Gemini);
        requiredTypes.Add(supervisorType);

        AnsiConsole.MarkupLine("[dim]Checking agents (use --detect for full test)...[/]");

        // Uses cached results if available, otherwise quick PATH check (no API credits)
        var statuses = await AgentDetector.DetectAllWithCacheAsync();
        var hasErrors = false;

        foreach (var type in requiredTypes)
        {
            var status = statuses.First(s => s.Type == type);

            if (!status.Installed)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {type}: not installed (install '{status.Executable}' CLI)");
                hasErrors = true;
            }
            else if (!status.Responsive)
            {
                AnsiConsole.MarkupLine($"[yellow]![/] {type}: installed but not responding ({status.Error})");
                // Warning only, don't block
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]✓[/] {type}: ready {(status.Version != null ? $"[dim]({status.Version})[/]" : "")}");
            }
        }

        if (hasErrors)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Cannot start: required agents not installed[/]");
            AnsiConsole.MarkupLine("[dim]Use --skip-detect to bypass this check[/]");
        }
        else
        {
            AnsiConsole.WriteLine();
        }

        return hasErrors;
    }
}
