using Asynkron.Swarm.Models;
using Asynkron.Swarm.UI;
using Spectre.Console;

namespace Asynkron.Swarm.Services;

public class RoundOrchestrator
{
    private readonly AgentRegistry _registry;
    private readonly WorktreeService _worktreeService;
    private readonly TodoService _todoService;
    private readonly AgentService _agentService;

    public RoundOrchestrator()
    {
        _registry = new AgentRegistry();
        _worktreeService = new WorktreeService();
        _todoService = new TodoService();
        _agentService = new AgentService(_registry);
    }

    public async Task RunAsync(SwarmOptions options)
    {
        var absoluteRepoPath = Path.GetFullPath(options.Repo);

        // Show initial info
        AnsiConsole.Write(new FigletText("Swarm").Color(Color.Cyan1));
        AnsiConsole.MarkupLine($"[bold]Repository:[/] {absoluteRepoPath}");
        AnsiConsole.MarkupLine($"[bold]Agents:[/] {options.Agents}");
        AnsiConsole.MarkupLine($"[bold]Agent Type:[/] {options.AgentType}");
        AnsiConsole.MarkupLine($"[bold]Time per round:[/] {options.Minutes} minutes");
        AnsiConsole.MarkupLine($"[bold]Max rounds:[/] {options.MaxRounds}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Starting in 2 seconds...[/]");
        await Task.Delay(2000);

        for (var round = 1; round <= options.MaxRounds; round++)
        {
            // Check if there are remaining items in todo
            var hasItems = await _todoService.HasRemainingItemsAsync(absoluteRepoPath, options.Todo);
            if (!hasItems)
            {
                AnsiConsole.MarkupLine("[green]All todo items completed! Swarm finished.[/]");
                break;
            }

            await RunRoundAsync(options, absoluteRepoPath, round);
        }

        AnsiConsole.MarkupLine("[bold green]Swarm orchestration complete.[/]");
    }

    private async Task RunRoundAsync(SwarmOptions options, string repoPath, int round)
    {
        // Clear registry for new round
        _agentService.RemoveAllAgents();

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[yellow]Round {round}[/]").RuleStyle("grey"));

        // Step 1: Create worktrees
        var worktreePaths = await _worktreeService.CreateWorktreesAsync(repoPath, round, options.Agents);

        try
        {
            // Step 2: Inject rivals into each worktree's todo
            foreach (var worktreePath in worktreePaths)
            {
                await _todoService.InjectRivalsAsync(worktreePath, options.Todo, worktreePaths);
            }

            // Step 3: Start worker agents
            var workers = new List<AgentInfo>();
            for (var i = 0; i < worktreePaths.Count; i++)
            {
                var worker = _agentService.StartWorker(
                    round,
                    i + 1,
                    worktreePaths[i],
                    options.Todo,
                    options.AgentType);
                workers.Add(worker);
            }

            // Step 4: Start supervisor agent
            var workerLogPaths = workers.Select(w => w.LogPath).ToList();
            var supervisor = _agentService.StartSupervisor(
                round,
                worktreePaths,
                workerLogPaths,
                repoPath,
                options.AgentType);

            // Step 5: Run the UI and wait for timeout
            using var cts = new CancellationTokenSource();
            using var ui = new SwarmUI(_registry);

            // Start UI in background
            var uiTask = ui.RunAsync(cts.Token);

            // Wait for the specified time
            var timeout = TimeSpan.FromMinutes(options.Minutes);
            var timeoutTask = Task.Delay(timeout, cts.Token);

            // Also monitor if all workers finish early
            var monitorTask = MonitorWorkersAsync(workers, cts.Token);

            // Wait for either timeout or all workers done
            await Task.WhenAny(timeoutTask, monitorTask);

            // Step 6: Kill all workers and append stopped marker
            await _agentService.KillAllWorkersAsync();

            // Remove dead workers from UI
            foreach (var worker in workers)
            {
                _agentService.RemoveAgent(worker);
            }

            // Step 7-10: Wait for supervisor to complete its work
            // Give supervisor time to evaluate (max 10 minutes)
            var supervisorTimeout = TimeSpan.FromMinutes(10);
            var supervisorComplete = await WaitForAgentAsync(supervisor, supervisorTimeout);

            if (!supervisorComplete)
            {
                _agentService.KillAgent(supervisor);
            }

            // Stop the UI
            cts.Cancel();
            await uiTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // Step 11: Cleanup
            _agentService.RemoveAgent(supervisor);
            await _worktreeService.DeleteWorktreesAsync(repoPath, worktreePaths);

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[green]Round {round} complete.[/]");
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error in round {round}: {ex.Message}[/]");

            // Cleanup on error
            _agentService.RemoveAllAgents();
            await _worktreeService.DeleteWorktreesAsync(repoPath, worktreePaths);
            throw;
        }
    }

    private static async Task MonitorWorkersAsync(List<AgentInfo> workers, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var allDone = workers.All(w => w.Process.HasExited);
            if (allDone)
            {
                return;
            }
            await Task.Delay(1000, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task<bool> WaitForAgentAsync(AgentInfo agent, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            if (agent.Process.HasExited)
            {
                return true;
            }
            await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return false;
    }
}
