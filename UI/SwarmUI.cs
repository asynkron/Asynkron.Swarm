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

    // Cache log content per agent
    private readonly Dictionary<string, LogCache> _logCaches = new();

    // Status messages
    private readonly List<string> _statusMessages = [];
    private string _currentPhase = "";
    private int _currentRound;
    private int _totalRounds;
    private TimeSpan _remainingTime;

    private const int LogTailLines = 50;
    private const int RefreshMs = 250;
    private const int MaxStatusMessages = 10;

    private class LogCache
    {
        public long LastPosition { get; set; }
        public long LastSize { get; set; }
        public List<string> Lines { get; } = new(LogTailLines + 10);
        public string CachedContent { get; set; } = "";
        public int TotalLineCount { get; set; }
    }

    public SwarmUI(AgentRegistry registry)
    {
        _registry = registry;

        // Initialize with existing agents
        foreach (var agent in _registry.GetAll())
        {
            _agentIds.Add(agent.Id);
            _logCaches[agent.Id] = new LogCache();
        }

        if (_agentIds.Count > 0)
        {
            _selectedAgentId = _agentIds[0];
            _selectedIndex = 0;
        }

        _registry.AgentAdded += OnAgentAdded;
        _registry.AgentRemoved += OnAgentRemoved;
        _registry.AgentStopped += OnAgentStopped;
    }

    private void OnAgentAdded(AgentInfo agent)
    {
        lock (_lock)
        {
            _agentIds.Add(agent.Id);
            _logCaches[agent.Id] = new LogCache();

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
                _logCaches.Remove(agent.Id);

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

    public void SetRound(int round, int total)
    {
        lock (_lock)
        {
            _currentRound = round;
            _totalRounds = total;
        }
    }

    public void SetPhase(string phase)
    {
        lock (_lock)
        {
            _currentPhase = phase;
            AddStatus(phase);
        }
    }

    public void SetRemainingTime(TimeSpan remaining)
    {
        lock (_lock)
        {
            _remainingTime = remaining;
        }
    }

    public void AddStatus(string message)
    {
        lock (_lock)
        {
            _statusMessages.Add($"[grey]{DateTime.Now:HH:mm:ss}[/] {message}");
            while (_statusMessages.Count > MaxStatusMessages)
            {
                _statusMessages.RemoveAt(0);
            }
        }
    }

    public async Task RunAsync(CancellationToken externalToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _cts.Token);
        var token = linkedCts.Token;

        // Start input handling task
        var inputTask = Task.Run(() => HandleInputAsync(token), token);

        try
        {
            // Clear and show initial render
            AnsiConsole.Clear();
            AnsiConsole.Write(BuildLayout());

            // Main render loop
            await AnsiConsole.Live(BuildLayout())
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                        await Task.Delay(RefreshMs, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }

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
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Main"));

        var main = layout["Main"].SplitColumns(
            new Layout("Left").Size(40),
            new Layout("Log"));

        main["Left"].SplitRows(
            new Layout("Agents"),
            new Layout("Status").Size(14));

        layout["Header"].Update(BuildHeader());
        main["Agents"].Update(BuildAgentList());
        main["Status"].Update(BuildStatusPanel());
        main["Log"].Update(BuildLogPanel());

        return layout;
    }

    private Panel BuildHeader()
    {
        int round, total;
        string phase;
        TimeSpan remaining;

        lock (_lock)
        {
            round = _currentRound;
            total = _totalRounds;
            phase = _currentPhase;
            remaining = _remainingTime;
        }

        var roundText = total > 0 ? $"Round [cyan]{round}[/]/[grey]{total}[/]" : "";
        var timeText = remaining > TimeSpan.Zero ? $"[yellow]{remaining:mm\\:ss}[/] remaining" : "";
        var phaseText = !string.IsNullOrEmpty(phase) ? $"[grey]│[/] {phase}" : "";

        var content = $"[bold cyan]SWARM[/] {roundText} {timeText} {phaseText}";

        return new Panel(new Markup(content))
            .Border(BoxBorder.None)
            .Expand();
    }

    private Panel BuildStatusPanel()
    {
        List<string> messages;
        lock (_lock)
        {
            messages = _statusMessages.ToList();
        }

        var content = messages.Count > 0
            ? string.Join("\n", messages)
            : "[grey]No status messages[/]";

        return new Panel(new Markup(content))
            .Header("[bold]Status[/]")
            .BorderColor(Color.Grey)
            .Expand();
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
            table.AddRow(" ", "[grey]No agents running[/]");
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

        var cache = _logCaches.GetValueOrDefault(agentId) ?? new LogCache();
        UpdateLogCache(agent.LogPath, cache);

        var statusText = agent.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]";
        var timeStamp = DateTime.Now.ToString("HH:mm:ss");
        var headerText = $"[bold]{agent.Name}[/] - {statusText} - [grey]{agent.Runtime}[/] - [grey]{timeStamp} ({cache.TotalLineCount} lines)[/]";

        return new Panel(new Text(cache.CachedContent))
            .Header(headerText)
            .BorderColor(agent.IsRunning ? Color.Green : Color.Red)
            .Expand();
    }

    private void UpdateLogCache(string logPath, LogCache cache)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                cache.CachedContent = "[Waiting for log file...]";
                return;
            }

            var fileInfo = new FileInfo(logPath);

            // Only re-read if file has changed
            if (fileInfo.Length == cache.LastSize)
            {
                return; // No changes, use cached content
            }

            // File grew - read new content
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fileInfo.Length > cache.LastSize && cache.LastPosition > 0)
            {
                // Incremental read - seek to last position
                fs.Seek(cache.LastPosition, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(fs);

            if (cache.LastPosition == 0)
            {
                // First read - read all
                cache.Lines.Clear();
            }

            while (reader.ReadLine() is { } line)
            {
                cache.Lines.Add(line);
                cache.TotalLineCount++;
            }

            cache.LastPosition = fs.Position;
            cache.LastSize = fileInfo.Length;

            // Keep only last N lines
            while (cache.Lines.Count > LogTailLines)
            {
                cache.Lines.RemoveAt(0);
            }

            // Strip ANSI codes (Text class handles display without markup interpretation)
            var cleanLines = cache.Lines.Select(StripAnsiCodes).ToList();
            cache.CachedContent = cleanLines.Count > 0
                ? string.Join(Environment.NewLine, cleanLines)
                : "[Empty log file]";
        }
        catch (Exception ex)
        {
            cache.CachedContent = $"[Error reading log: {ex.Message}]";
        }
    }

    private static string StripAnsiCodes(string input)
    {
        // Strip ANSI escape codes (colors, cursor movement, etc.)
        return System.Text.RegularExpressions.Regex.Replace(
            input,
            @"\x1B\[[0-9;]*[A-Za-z]",
            "");
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
