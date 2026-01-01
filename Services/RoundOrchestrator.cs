using Asynkron.Swarm.Agents;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.UI;

namespace Asynkron.Swarm.Services;

public class RoundOrchestrator
{
    private readonly AgentRegistry _registry;
    private SwarmSession? _session;
    private AgentService? _agentService;
    private SwarmUI? _ui;

    public RoundOrchestrator()
    {
        _registry = new AgentRegistry();
    }

    public void KillAllAgents()
    {
        _ui?.AddStatus("[red]Killing all agents...[/]");
        foreach (var agent in _registry.GetAll())
        {
            try
            {
                agent.Stop();
                _ui?.AddStatus($"Killed {agent.Name}");
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
        _registry.Clear();
    }

    public async Task RunAsync(SwarmOptions options, string? resumeSessionId = null)
    {
        var absoluteRepoPath = Path.GetFullPath(options.Repo);

        // Create or resume session
        if (resumeSessionId != null)
        {
            _session = SwarmSession.Load(resumeSessionId);
            if (_session == null)
            {
                throw new InvalidOperationException($"Session not found: {resumeSessionId}");
            }
        }
        else
        {
            _session = SwarmSession.Create(options);
        }

        _agentService = new AgentService(_registry, _session);

        using var cts = new CancellationTokenSource();
        _ui = new SwarmUI(_registry, options.Arena, options.Autopilot, _session.SessionId);

        // Start UI immediately
        var uiTask = _ui.RunAsync(cts.Token);

        _ui.AddStatus($"Session: [bold]{_session.SessionId}[/]");
        _ui.AddStatus($"Path: {_session.SessionPath}");
        _ui.AddStatus($"Repository: {absoluteRepoPath}");
        _ui.AddStatus($"Workers: {options.ClaudeWorkers} Claude + {options.CodexWorkers} Codex + {options.CopilotWorkers} Copilot + {options.GeminiWorkers} Gemini");
        _ui.AddStatus($"Supervisor: {options.SupervisorType}");
        _ui.AddStatus($"Time limit: {options.Minutes} min");
        if (options.Arena)
        {
            _ui.AddStatus("[yellow]Arena mode: Timed rounds, supervisor picks winner[/]");
        }

        try
        {
            if (options.Arena)
            {
                for (var round = 1; round <= options.MaxRounds; round++)
                {
                    var hasItems = await TodoService.HasRemainingItemsAsync(absoluteRepoPath, options.Todo);
                    if (!hasItems)
                    {
                        _ui.AddStatus("[green]All todo items completed![/]");
                        break;
                    }

                    await RunArenaRoundAsync(options, absoluteRepoPath, round, cts.Token);
                }

                _ui.AddStatus("[bold green]Arena complete.[/]");
            }
            else
            {
                await RunDefaultAsync(options, absoluteRepoPath, cts.Token);
                _ui.AddStatus("[bold green]All workers finished.[/]");
            }
            await Task.Delay(2000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            cts.Cancel();
            await uiTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ui.Dispose();
        }
    }

    private async Task RunDefaultAsync(SwarmOptions options, string repoPath, CancellationToken token)
    {
        _agentService!.RemoveAllAgents();

        _ui!.SetRound(1, 1);
        _ui.SetPhase("Creating worktrees...");

        // Get worktree paths from session
        var worktreePaths = Enumerable.Range(1, options.TotalWorkers)
            .Select(i => _session!.GetWorktreePath(i))
            .ToList();

        // Create worktrees
        await WorktreeService.CreateWorktreesAsync(repoPath, worktreePaths);

        // Generate timestamp for unique branch names
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // Start worker agents with autopilot enabled
        _ui.SetPhase("Starting workers...");
        var workers = new List<WorkerAgent>();
        for (var i = 0; i < worktreePaths.Count; i++)
        {
            var agentType = GetAgentType(i, options);
            var branchName = $"autopilot/worker{i + 1}-{timestamp}";
            var worker = _agentService.CreateWorker(
                i + 1,
                options.Todo,
                agentType,
                autopilot: true,
                branchName: branchName);
            worker.Start();
            workers.Add(worker);
            _ui.AddStatus($"Started {worker.Name} ({agentType}) -> branch: {branchName}");
        }

        // Start supervisor agent
        _ui.SetPhase("Starting supervisor...");
        var workerLogPaths = workers.Select(w => w.LogPath).ToList();
        var supervisor = _agentService.CreateSupervisor(
            worktreePaths,
            workerLogPaths,
            repoPath,
            options.SupervisorType,
            autopilot: true);
        supervisor.Start();
        _ui.AddStatus($"Started Supervisor ({options.SupervisorType}) - monitoring mode");

        // Subscribe to restart events
        supervisor.OnRestarted += a => _ui.AddStatus($"[#98c379]Restarted {a.Name}[/]");
        foreach (var worker in workers)
        {
            worker.OnRestarted += a => _ui.AddStatus($"[#98c379]Restarted {a.Name}[/]");
        }

        // Wait for workers with optional timeout
        var timeout = TimeSpan.FromMinutes(options.Minutes);
        var endTime = DateTime.Now.Add(timeout);
        var timedOut = false;

        _ui.SetPhase("Workers running...");
        while (!token.IsCancellationRequested)
        {
            var remaining = endTime - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                _ui.AddStatus("[yellow]Time limit reached, stopping workers[/]");
                timedOut = true;
                break;
            }

            var runningCount = workers.Count(w => w.IsRunning);
            if (runningCount == 0)
            {
                _ui.AddStatus("[green]All workers completed[/]");
                break;
            }

            _ui.SetRemainingTime(remaining);
            _ui.SetPhase($"Workers running: {runningCount}/{workers.Count} active");
            await Task.Delay(1000, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Stop workers if timed out
        if (timedOut)
        {
            await _agentService.StopAllWorkersAsync();
        }
        _ui.SetRemainingTime(TimeSpan.Zero);

        // Wait for supervisor to finish
        _ui.SetPhase("Waiting for supervisor final summary...");
        var supervisorTimeout = TimeSpan.FromMinutes(2);
        var supervisorComplete = await WaitForAgentAsync(supervisor, supervisorTimeout);
        if (!supervisorComplete)
        {
            _ui.AddStatus("Supervisor timed out, stopping");
            supervisor.Stop();
        }
        else
        {
            _ui.AddStatus("Supervisor completed");
        }

        // Cleanup agents (but NOT worktrees - session folder persists)
        _ui.SetPhase("Done");
        _agentService.RemoveAgent(supervisor);
        foreach (var worker in workers)
        {
            _agentService.RemoveAgent(worker);
        }
    }

    private async Task RunArenaRoundAsync(SwarmOptions options, string repoPath, int round, CancellationToken token)
    {
        _agentService!.RemoveAllAgents();

        _ui!.SetRound(round, options.MaxRounds);
        _ui.SetPhase("Creating worktrees...");

        // Get worktree paths from session
        var worktreePaths = Enumerable.Range(1, options.TotalWorkers)
            .Select(i => _session!.GetWorktreePath(i))
            .ToList();

        // Create worktrees
        await WorktreeService.CreateWorktreesAsync(repoPath, worktreePaths);

        // Start worker agents
        _ui.SetPhase("Starting workers...");
        var workers = new List<WorkerAgent>();
        for (var i = 0; i < worktreePaths.Count; i++)
        {
            var agentType = GetAgentType(i, options);
            var worker = _agentService.CreateWorker(
                i + 1,
                options.Todo,
                agentType);
            worker.Start();
            workers.Add(worker);
            _ui.AddStatus($"Started {worker.Name} ({agentType})");
        }

        // Start supervisor agent
        _ui.SetPhase("Starting supervisor...");
        var workerLogPaths = workers.Select(w => w.LogPath).ToList();
        var supervisor = _agentService.CreateSupervisor(
            worktreePaths,
            workerLogPaths,
            repoPath,
            options.SupervisorType);
        supervisor.Start();
        _ui.AddStatus($"Started Supervisor ({options.SupervisorType})");

        // Subscribe to restart events
        supervisor.OnRestarted += a => _ui.AddStatus($"[#98c379]Restarted {a.Name}[/]");
        foreach (var worker in workers)
        {
            worker.OnRestarted += a => _ui.AddStatus($"[#98c379]Restarted {a.Name}[/]");
        }

        // Wait for timeout with countdown
        _ui.SetPhase("Workers competing...");
        var timeout = TimeSpan.FromMinutes(options.Minutes);
        var endTime = DateTime.Now.Add(timeout);

        while (DateTime.Now < endTime && !token.IsCancellationRequested)
        {
            var remaining = endTime - DateTime.Now;
            _ui.SetRemainingTime(remaining);

            if (workers.All(w => !w.IsRunning))
            {
                _ui.AddStatus("All workers finished early");
                break;
            }

            await Task.Delay(250, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Stop all workers
        _ui.SetPhase("Stopping workers...");
        _ui.SetRemainingTime(TimeSpan.Zero);
        await _agentService.StopAllWorkersAsync();

        foreach (var worker in workers)
        {
            _agentService.RemoveAgent(worker);
        }

        // Wait for supervisor to complete
        _ui.SetPhase("Supervisor evaluating...");
        var supervisorTimeout = TimeSpan.FromMinutes(10);
        var supervisorComplete = await WaitForAgentAsync(supervisor, supervisorTimeout);

        if (!supervisorComplete)
        {
            _ui.AddStatus("Supervisor timed out");
            supervisor.Stop();
        }
        else
        {
            _ui.AddStatus("Supervisor completed");
        }

        // Cleanup agents (but NOT worktrees)
        _ui.SetPhase("Round complete");
        _agentService.RemoveAgent(supervisor);

        _ui.AddStatus($"[green]Round {round} complete[/]");
    }

    private static async Task<bool> WaitForAgentAsync(AgentBase agent, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            if (!agent.IsRunning)
            {
                return true;
            }

            await Task.Delay(1000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return false;
    }

    private static AgentType GetAgentType(int index, SwarmOptions options)
    {
        if (index < options.ClaudeWorkers)
            return AgentType.Claude;
        if (index < options.ClaudeWorkers + options.CodexWorkers)
            return AgentType.Codex;
        if (index < options.ClaudeWorkers + options.CodexWorkers + options.CopilotWorkers)
            return AgentType.Copilot;
        return AgentType.Gemini;
    }
}
