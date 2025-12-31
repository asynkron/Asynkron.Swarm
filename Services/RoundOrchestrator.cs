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
        _ui = new SwarmUI(_registry);

        // Start UI immediately
        var uiTask = _ui.RunAsync(cts.Token);

        _ui.SetRound(0, options.MaxRounds);
        _ui.AddStatus($"Repository: {absoluteRepoPath}");
        _ui.AddStatus($"Agents: {options.Agents} x {options.AgentType}");
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
        var worktreePaths = await _worktreeService.CreateWorktreesAsync(repoPath, round, options.Agents);
        _currentWorktreePaths = worktreePaths;

        try
        {
            // Step 2: Inject rivals into each worktree's todo
            _ui.SetPhase("Injecting rivals...");
            foreach (var worktreePath in worktreePaths)
            {
                await TodoService.InjectRivalsAsync(worktreePath, options.Todo, worktreePaths);
            }

            // Step 3: Start worker agents
            _ui.SetPhase("Starting workers...");
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
                _ui.AddStatus($"Started {worker.Name}");
            }

            // Step 4: Start supervisor agent
            _ui.SetPhase("Starting supervisor...");
            var workerLogPaths = workers.Select(w => w.LogPath).ToList();
            var supervisor = _agentService.StartSupervisor(
                round,
                worktreePaths,
                workerLogPaths,
                repoPath,
                options.AgentType);
            _ui.AddStatus("Started Supervisor");

            // Step 5: Wait for timeout with countdown
            _ui.SetPhase("Workers competing...");
            var timeout = TimeSpan.FromMinutes(options.Minutes);
            var endTime = DateTime.Now.Add(timeout);

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

                await Task.Delay(250, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            // Step 6: Kill all workers and append stopped marker
            _ui.SetPhase("Stopping workers...");
            _ui.SetRemainingTime(TimeSpan.Zero);
            await _agentService.KillAllWorkersAsync();

            // Remove dead workers from UI
            foreach (var worker in workers)
            {
                _agentService.RemoveAgent(worker);
            }

            // Step 7-10: Wait for supervisor to complete its work
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

            // Step 11: Cleanup
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
