using Asynkron.Swarm.Models;
using Asynkron.Swarm.UI;

namespace Asynkron.Swarm.Services;

public class RoundOrchestrator
{
    private readonly AgentRegistry _registry;
    private readonly WorktreeService _worktreeService;
    private readonly TodoService _todoService;
    private readonly AgentService _agentService;
    private List<string> _currentWorktreePaths = [];
    private string? _currentRepoPath;
    private SwarmUI? _ui;

    public RoundOrchestrator()
    {
        _registry = new AgentRegistry();
        _worktreeService = new WorktreeService();
        _todoService = new TodoService();
        _agentService = new AgentService(_registry);
    }

    public void KillAllAgents()
    {
        _ui?.AddStatus("[red]Killing all agents...[/]");
        foreach (var agent in _registry.GetAll())
        {
            try
            {
                if (!agent.Process.HasExited)
                {
                    agent.Process.Kill(entireProcessTree: true);
                    _ui?.AddStatus($"Killed {agent.Name}");
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
        _registry.Clear();
    }

    public async Task CleanupWorktreesAsync()
    {
        if (_currentRepoPath != null && _currentWorktreePaths.Count > 0)
        {
            _ui?.AddStatus("Cleaning up worktrees...");
            await WorktreeService.DeleteWorktreesAsync(_currentRepoPath, _currentWorktreePaths);
            _currentWorktreePaths.Clear();
        }
    }

    public async Task RunAsync(SwarmOptions options)
    {
        var absoluteRepoPath = Path.GetFullPath(options.Repo);

        using var cts = new CancellationTokenSource();
        _ui = new SwarmUI(_registry, _agentService);

        // Start UI immediately
        var uiTask = _ui.RunAsync(cts.Token);

        _ui.SetRound(0, options.MaxRounds);
        _ui.AddStatus($"Repository: {absoluteRepoPath}");
        _ui.AddStatus($"Workers: {options.ClaudeWorkers} Claude + {options.CodexWorkers} Codex");
        _ui.AddStatus($"Supervisor: {options.SupervisorType}");
        _ui.AddStatus($"Time per round: {options.Minutes} min");

        try
        {
            for (var round = 1; round <= options.MaxRounds; round++)
            {
                // Check if there are remaining items in todo
                var hasItems = await TodoService.HasRemainingItemsAsync(absoluteRepoPath, options.Todo);
                if (!hasItems)
                {
                    _ui.AddStatus("[green]All todo items completed![/]");
                    break;
                }

                await RunRoundAsync(options, absoluteRepoPath, round, cts.Token);
            }

            _ui.AddStatus("[bold green]Swarm orchestration complete.[/]");
            await Task.Delay(2000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            cts.Cancel();
            await uiTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ui.Dispose();
        }
    }

    private async Task RunRoundAsync(SwarmOptions options, string repoPath, int round, CancellationToken token)
    {
        // Clear registry for new round
        _agentService.RemoveAllAgents();
        _currentRepoPath = repoPath;

        _ui!.SetRound(round, options.MaxRounds);
        _ui.SetPhase("Creating worktrees...");

        // Step 1: Create worktrees
        var worktreePaths = await _worktreeService.CreateWorktreesAsync(repoPath, round, options.TotalWorkers);
        _currentWorktreePaths = worktreePaths;

        try
        {
            // Step 2: Inject rivals into each worktree's todo
            _ui.SetPhase("Injecting rivals...");
            foreach (var worktreePath in worktreePaths)
            {
                await TodoService.InjectRivalsAsync(worktreePath, options.Todo, worktreePaths);
            }

            // Step 3: Create shared communication file for this round
            var swarmDir = Path.Combine(repoPath, ".swarm");
            Directory.CreateDirectory(swarmDir);
            var sharedFilePath = Path.Combine(swarmDir, $"round{round}-shared.md");
            await File.WriteAllTextAsync(sharedFilePath, $"# Shared Agent Communication - Round {round}\n\nAgents should document all their key findings below.\n\n---\n\n", token);
            _ui.AddStatus($"Shared file: {sharedFilePath}");

            // Step 4: Start worker agents (Claude first, then Codex)
            _ui.SetPhase("Starting workers...");
            var workers = new List<AgentInfo>();
            for (var i = 0; i < worktreePaths.Count; i++)
            {
                var agentType = i < options.ClaudeWorkers ? AgentType.Claude : AgentType.Codex;
                var worker = _agentService.StartWorker(
                    round,
                    i + 1,
                    worktreePaths[i],
                    options.Todo,
                    sharedFilePath,
                    agentType);
                workers.Add(worker);
                _ui.AddStatus($"Started {worker.Name} ({agentType})");
            }

            // Step 5: Start supervisor agent
            _ui.SetPhase("Starting supervisor...");
            var workerLogPaths = workers.Select(w => w.LogPath).ToList();
            var supervisor = _agentService.StartSupervisor(
                round,
                worktreePaths,
                workerLogPaths,
                repoPath,
                options.SupervisorType);
            _ui.AddStatus($"Started Supervisor ({options.SupervisorType})");

            // Step 6: Wait for timeout with countdown
            _ui.SetPhase("Workers competing...");
            var timeout = TimeSpan.FromMinutes(options.Minutes);
            var endTime = DateTime.Now.Add(timeout);
            var lastSupervisorRestart = DateTime.MinValue;

            while (DateTime.Now < endTime && !token.IsCancellationRequested)
            {
                var remaining = endTime - DateTime.Now;
                _ui.SetRemainingTime(remaining);

                // Check if all workers finished early
                if (workers.All(w => w.Process.HasExited))
                {
                    _ui.AddStatus("All workers finished early");
                    break;
                }

                // Check if supervisor died and restart it (with 30s cooldown)
                if (supervisor.Process.HasExited && DateTime.Now - lastSupervisorRestart > TimeSpan.FromSeconds(30))
                {
                    _ui.AddStatus("[yellow]Supervisor died, restarting...[/]");
                    _agentService.RemoveAgent(supervisor);
                    supervisor = _agentService.StartSupervisor(
                        round,
                        worktreePaths,
                        workerLogPaths,
                        repoPath,
                        options.SupervisorType);
                    _ui.AddStatus("Restarted Supervisor");
                    lastSupervisorRestart = DateTime.Now;
                }

                await Task.Delay(250, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            // Step 7: Kill all workers and append stopped marker
            _ui.SetPhase("Stopping workers...");
            _ui.SetRemainingTime(TimeSpan.Zero);
            await _agentService.KillAllWorkersAsync();

            // Remove dead workers from UI
            foreach (var worker in workers)
            {
                _agentService.RemoveAgent(worker);
            }

            // Step 8: Wait for supervisor to complete its work
            _ui.SetPhase("Supervisor evaluating...");
            var supervisorTimeout = TimeSpan.FromMinutes(10);
            var supervisorComplete = await WaitForAgentAsync(supervisor, supervisorTimeout);

            if (!supervisorComplete)
            {
                _ui.AddStatus("Supervisor timed out");
                _agentService.KillAgent(supervisor);
            }
            else
            {
                _ui.AddStatus("Supervisor completed");
            }

            // Step 9: Cleanup
            _ui.SetPhase("Cleaning up...");
            _agentService.RemoveAgent(supervisor);
            await WorktreeService.DeleteWorktreesAsync(repoPath, worktreePaths);
            _currentWorktreePaths.Clear();

            _ui.AddStatus($"[green]Round {round} complete[/]");
        }
        catch (Exception ex)
        {
            _ui.AddStatus($"[red]Error: {ex.Message}[/]");

            // Cleanup on error
            _agentService.RemoveAllAgents();
            await WorktreeService.DeleteWorktreesAsync(repoPath, worktreePaths);
            throw;
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
