using System.Globalization;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Services;
using Spectre.Console;

namespace Asynkron.Swarm.UI;

public sealed class SwarmUI : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

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
    private const int RefreshMs = 20;
    private const int MaxStatusMessages = 10;

    // Reusable layout structure
    private readonly Layout _layout;

    // Cached panels - only rebuild when dirty
    private Panel? _cachedHeader;
    private Panel? _cachedStatus;
    private Panel? _cachedAgents;
    private Panel? _cachedLog;

    // Dirty flags
    private bool _headerDirty = true;
    private bool _statusDirty = true;
    private bool _agentsDirty = true;
    private bool _logDirty = true;

    private sealed class LogCache
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

        // Create layout structure once
        _layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Main"));

        _layout["Main"].SplitColumns(
            new Layout("Left").Size(40),
            new Layout("Log"));

        _layout["Main"]["Left"].SplitRows(
            new Layout("Agents"),
            new Layout("Status").Size(14));
    }

    private void OnAgentAdded(AgentInfo agent)
    {
        lock (_lock)
        {
            _agentIds.Add(agent.Id);
            _logCaches[agent.Id] = new LogCache();
            _agentsDirty = true;

            if (_selectedAgentId == null)
            {
                _selectedAgentId = agent.Id;
                _selectedIndex = 0;
                _logDirty = true;
            }
        }
    }

    private void OnAgentRemoved(AgentInfo agent)
    {
        lock (_lock)
        {
            var index = _agentIds.IndexOf(agent.Id);
            if (index < 0) return;
            _agentIds.RemoveAt(index);
            _logCaches.Remove(agent.Id);
            _agentsDirty = true;

            if (_selectedAgentId != agent.Id) return;
            _selectedIndex = Math.Max(0, Math.Min(_selectedIndex, _agentIds.Count - 1));
            _selectedAgentId = _agentIds.Count > 0 ? _agentIds[_selectedIndex] : null;
            _logDirty = true;
        }
    }

    private void OnAgentStopped(AgentInfo agent)
    {
        lock (_lock)
        {
            _agentsDirty = true;
            if (_selectedAgentId == agent.Id)
            {
                _logDirty = true;
            }
        }
    }

    public void SetRound(int round, int total)
    {
        lock (_lock)
        {
            if (_currentRound != round || _totalRounds != total)
            {
                _currentRound = round;
                _totalRounds = total;
                _headerDirty = true;
            }
        }
    }

    public void SetPhase(string phase)
    {
        lock (_lock)
        {
            if (_currentPhase != phase)
            {
                _currentPhase = phase;
                _headerDirty = true;
            }
            AddStatusInternal(phase);
        }
    }

    public void SetRemainingTime(TimeSpan remaining)
    {
        lock (_lock)
        {
            if (_remainingTime != remaining)
            {
                _remainingTime = remaining;
                _headerDirty = true;
            }
        }
    }

    public void AddStatus(string message)
    {
        lock (_lock)
        {
            AddStatusInternal(message);
        }
    }

    private void AddStatusInternal(string message)
    {
        _statusMessages.Add($"[grey]{DateTime.Now:HH:mm:ss}[/] {message}");
        while (_statusMessages.Count > MaxStatusMessages)
        {
            _statusMessages.RemoveAt(0);
        }
        _statusDirty = true;
    }

    public async Task RunAsync(CancellationToken externalToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _cts.Token);
        var token = linkedCts.Token;

        // Start input handling task
        var inputTask = Task.Run(() => HandleInputAsync(token), token);

        try
        {
            // Initial update of all regions
            UpdateLayoutRegions();

            // Clear and show initial render
            AnsiConsole.Clear();

            // Main render loop - reuse same layout, just update regions
            await AnsiConsole.Live(_layout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        UpdateLayoutRegions();
                        ctx.UpdateTarget(_layout);
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
                                _agentsDirty = true;
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.DownArrow:
                        case ConsoleKey.J:
                            if (_selectedIndex < _agentIds.Count - 1)
                            {
                                _selectedIndex++;
                                _selectedAgentId = _agentIds[_selectedIndex];
                                _agentsDirty = true;
                                _logDirty = true;
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

    private void UpdateLayoutRegions()
    {
        lock (_lock)
        {
            if (_headerDirty || _cachedHeader == null)
            {
                _cachedHeader = BuildHeader();
                _layout["Header"].Update(_cachedHeader);
                _headerDirty = false;
            }

            if (_agentsDirty || _cachedAgents == null)
            {
                _cachedAgents = BuildAgentList();
                _layout["Main"]["Left"]["Agents"].Update(_cachedAgents);
                _agentsDirty = false;
            }

            if (_statusDirty || _cachedStatus == null)
            {
                _cachedStatus = BuildStatusPanel();
                _layout["Main"]["Left"]["Status"].Update(_cachedStatus);
                _statusDirty = false;
            }

            // Log panel: check if log content changed
            var logChanged = CheckLogChanged();
            if (_logDirty || logChanged || _cachedLog == null)
            {
                _cachedLog = BuildLogPanel();
                _layout["Main"]["Log"].Update(_cachedLog);
                _logDirty = false;
            }
        }
    }

    private bool CheckLogChanged()
    {
        if (_selectedAgentId == null) return false;
        var cache = _logCaches.GetValueOrDefault(_selectedAgentId);
        if (cache == null) return false;

        var agent = _registry.Get(_selectedAgentId);
        if (agent == null) return false;

        try
        {
            if (!File.Exists(agent.LogPath)) return false;
            var fileInfo = new FileInfo(agent.LogPath);
            return fileInfo.Length != cache.LastSize;
        }
        catch
        {
            return false;
        }
    }

    private Panel BuildHeader()
    {
        // Called with _lock held
        var roundText = _totalRounds > 0 ? $"Round [cyan]{_currentRound}[/]/[grey]{_totalRounds}[/]" : "";
        var timeText = _remainingTime > TimeSpan.Zero ? $"[yellow]{_remainingTime:mm\\:ss}[/] remaining" : "";
        var phaseText = !string.IsNullOrEmpty(_currentPhase) ? $"[grey]│[/] {_currentPhase}" : "";

        var content = $"[bold cyan]SWARM[/] {roundText} {timeText} {phaseText}";

        return new Panel(new Markup(content))
            .Border(BoxBorder.None)
            .Expand();
    }

    private Panel BuildStatusPanel()
    {
        // Called with _lock held
        var content = _statusMessages.Count > 0
            ? string.Join("\n", _statusMessages)
            : "[grey]No status messages[/]";

        return new Panel(new Markup(content))
            .Header("[bold]Status[/]")
            .BorderColor(Color.Grey)
            .Expand();
    }

    private Panel BuildAgentList()
    {
        // Called with _lock held
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Status", c => c.Width(3))
            .AddColumn("Agent");

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
        // Called with _lock held
        if (_selectedAgentId == null)
        {
            return new Panel("[grey]Select an agent to view logs[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(Color.Grey)
                .Expand();
        }

        var agent = _registry.Get(_selectedAgentId);
        if (agent == null)
        {
            return new Panel("[grey]Agent not found[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(Color.Grey)
                .Expand();
        }

        var cache = _logCaches.GetValueOrDefault(_selectedAgentId) ?? new LogCache();
        UpdateLogCache(agent.LogPath, cache);

        var statusText = agent.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]";
        var timeStamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var headerText = $"[bold]{agent.Name}[/] - {statusText} - [grey]{agent.Runtime}[/] - [grey]{timeStamp} ({cache.TotalLineCount} lines)[/]";

        return new Panel(new Text(cache.CachedContent))
            .Header(headerText)
            .BorderColor(agent.IsRunning ? Color.Green : Color.Red)
            .Expand();
    }

    private static void UpdateLogCache(string logPath, LogCache cache)
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
