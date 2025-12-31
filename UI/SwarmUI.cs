using System.Text;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Services;
using Spectre.Console;

namespace Asynkron.Swarm.UI;

public class SwarmUI : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    private int _selectedIndex;
    private string? _selectedAgentId;
    private readonly List<string> _agentIds = [];
    private readonly Dictionary<string, long> _logPositions = new();

    private const int LogTailLines = 50;
    private const int RefreshMs = 500;

    public SwarmUI(AgentRegistry registry)
    {
        _registry = registry;

        _registry.AgentAdded += OnAgentAdded;
        _registry.AgentRemoved += OnAgentRemoved;
        _registry.AgentStopped += OnAgentStopped;
    }

    private void OnAgentAdded(AgentInfo agent)
    {
        lock (_lock)
        {
            _agentIds.Add(agent.Id);
            _logPositions[agent.Id] = 0;

            if (_selectedAgentId == null)
            {
                _selectedAgentId = agent.Id;
                _selectedIndex = 0;
            }
        }
    }

    private void OnAgentRemoved(AgentInfo agent)
    {
        lock (_lock)
        {
            var index = _agentIds.IndexOf(agent.Id);
            if (index >= 0)
            {
                _agentIds.RemoveAt(index);
                _logPositions.Remove(agent.Id);

                if (_selectedAgentId == agent.Id)
                {
                    _selectedIndex = Math.Max(0, Math.Min(_selectedIndex, _agentIds.Count - 1));
                    _selectedAgentId = _agentIds.Count > 0 ? _agentIds[_selectedIndex] : null;
                }
            }
        }
    }

    private void OnAgentStopped(AgentInfo agent)
    {
        // Agent stopped but not removed yet - UI will show it as stopped
    }

    public async Task RunAsync(CancellationToken externalToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _cts.Token);
        var token = linkedCts.Token;

        // Start input handling task
        var inputTask = Task.Run(() => HandleInputAsync(token), token);

        // Main render loop
        await AnsiConsole.Live(BuildLayout())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!token.IsCancellationRequested)
                {
                    ctx.UpdateTarget(BuildLayout());
                    await Task.Delay(RefreshMs, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            });

        await inputTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task HandleInputAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                lock (_lock)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                        case ConsoleKey.K:
                            if (_selectedIndex > 0)
                            {
                                _selectedIndex--;
                                _selectedAgentId = _agentIds[_selectedIndex];
                            }
                            break;

                        case ConsoleKey.DownArrow:
                        case ConsoleKey.J:
                            if (_selectedIndex < _agentIds.Count - 1)
                            {
                                _selectedIndex++;
                                _selectedAgentId = _agentIds[_selectedIndex];
                            }
                            break;

                        case ConsoleKey.Q:
                            _cts.Cancel();
                            break;
                    }
                }
            }

            await Task.Delay(50, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private Layout BuildLayout()
    {
        var layout = new Layout("Root")
            .SplitColumns(
                new Layout("Agents").Size(35),
                new Layout("Log"));

        layout["Agents"].Update(BuildAgentList());
        layout["Log"].Update(BuildLogPanel());

        return layout;
    }

    private Panel BuildAgentList()
    {
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Status", c => c.Width(3))
            .AddColumn("Agent");

        lock (_lock)
        {
            for (var i = 0; i < _agentIds.Count; i++)
            {
                var agentId = _agentIds[i];
                var agent = _registry.Get(agentId);
                if (agent == null) continue;

                var isSelected = i == _selectedIndex;
                var statusIcon = agent.IsRunning ? "[green]●[/]" : "[red]○[/]";
                var kindIcon = agent.Kind == AgentKind.Supervisor ? "[yellow]S[/]" : "[cyan]W[/]";

                var name = isSelected
                    ? $"[bold reverse] {kindIcon} {agent.Name} [/]"
                    : $" {kindIcon} {agent.Name}";

                table.AddRow(statusIcon, name);
            }
        }

        if (table.Rows.Count == 0)
        {
            table.AddRow("[grey]", "[grey]No agents running[/]");
        }

        return new Panel(table)
            .Header("[bold]Agents[/] [grey](↑/↓ to select, q to quit)[/]")
            .BorderColor(Color.Grey)
            .Expand();
    }

    private Panel BuildLogPanel()
    {
        string? agentId;
        lock (_lock)
        {
            agentId = _selectedAgentId;
        }

        if (agentId == null)
        {
            return new Panel("[grey]Select an agent to view logs[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(Color.Grey)
                .Expand();
        }

        var agent = _registry.Get(agentId);
        if (agent == null)
        {
            return new Panel("[grey]Agent not found[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(Color.Grey)
                .Expand();
        }

        var logContent = ReadLogTail(agent.LogPath, LogTailLines);
        var statusText = agent.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]";
        var headerText = $"[bold]{agent.Name}[/] - {statusText} - [grey]{agent.Runtime}[/]";

        return new Panel(new Text(logContent))
            .Header(headerText)
            .BorderColor(agent.IsRunning ? Color.Green : Color.Red)
            .Expand();
    }

    private static string ReadLogTail(string logPath, int lines)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return "[Waiting for log file...]";
            }

            // Read file with shared access
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            var allLines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                allLines.Add(line);
            }

            var tailLines = allLines.TakeLast(lines).ToList();
            return tailLines.Count > 0
                ? string.Join(Environment.NewLine, tailLines)
                : "[Empty log file]";
        }
        catch (Exception ex)
        {
            return $"[Error reading log: {ex.Message}]";
        }
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        _registry.AgentAdded -= OnAgentAdded;
        _registry.AgentRemoved -= OnAgentRemoved;
        _registry.AgentStopped -= OnAgentStopped;
        _cts.Dispose();
    }
}
