using System.Globalization;
using Asynkron.Swarm.IO;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Asynkron.Swarm.UI;

public sealed class SwarmUI : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly Services.AgentService? _agentService;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

    private int _selectedIndex;
    private string? _selectedAgentId;
    private readonly List<string> _agentIds = [];

    // Focus and scroll state
    private enum FocusPanel { Agents, Log }
    private FocusPanel _focus = FocusPanel.Agents;
    private int _logScrollOffset; // 0 = bottom (most recent), positive = scroll up

    // Async file tailers per agent
    private readonly Dictionary<string, AsyncFileTailer> _tailers = new();

    // Liveness tracking per agent
    private readonly Dictionary<string, int> _lastLineCount = new();
    private readonly Dictionary<string, int> _spinnerFrame = new();
    private readonly Dictionary<string, DateTime> _lastHeartbeat = new();
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // Idle timeout thresholds
    private static readonly TimeSpan SupervisorIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerIdleTimeout = TimeSpan.FromSeconds(180);

    // Status messages
    private readonly List<string> _statusMessages = [];
    private string _currentPhase = "";
    private int _currentRound;
    private int _totalRounds;
    private TimeSpan _remainingTime;


    private const int RefreshMs = 20;
    private const int MaxStatusMessages = 10;
    private const int LogDisplayLines = 50;

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

    public SwarmUI(AgentRegistry registry, Services.AgentService? agentService = null)
    {
        _registry = registry;
        _agentService = agentService;

        // Initialize with existing agents
        foreach (var agent in _registry.GetAll())
        {
            _agentIds.Add(agent.Id);
            var mode = GetTailerMode(agent);
            var tailer = new AsyncFileTailer(agent.LogPath, maxLines: 500, mode: mode);
            tailer.Start();
            _tailers[agent.Id] = tailer;
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
            var mode = GetTailerMode(agent);
            var tailer = new AsyncFileTailer(agent.LogPath, maxLines: 500, mode: mode);
            tailer.Start();
            _tailers[agent.Id] = tailer;
            _lastHeartbeat[agent.Id] = DateTime.Now;
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

            if (_tailers.TryGetValue(agent.Id, out var tailer))
            {
                tailer.Dispose();
                _tailers.Remove(agent.Id);
            }

            _lastLineCount.Remove(agent.Id);
            _spinnerFrame.Remove(agent.Id);
            _lastHeartbeat.Remove(agent.Id);

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
        _statusMessages.Add($"[#5c6370]{DateTime.Now:HH:mm:ss}[/] {message}");
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
                        case ConsoleKey.Tab:
                            _focus = _focus == FocusPanel.Agents ? FocusPanel.Log : FocusPanel.Agents;
                            _agentsDirty = true;
                            _logDirty = true;
                            break;

                        case ConsoleKey.UpArrow:
                        case ConsoleKey.K:
                            if (_focus == FocusPanel.Agents)
                            {
                                if (_selectedIndex > 0)
                                {
                                    _selectedIndex--;
                                    _selectedAgentId = _agentIds[_selectedIndex];
                                    _logScrollOffset = 0; // Reset scroll when changing agent
                                    _agentsDirty = true;
                                    _logDirty = true;
                                }
                            }
                            else
                            {
                                // Scroll log up (show older content)
                                _logScrollOffset += 5;
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.DownArrow:
                        case ConsoleKey.J:
                            if (_focus == FocusPanel.Agents)
                            {
                                if (_selectedIndex < _agentIds.Count - 1)
                                {
                                    _selectedIndex++;
                                    _selectedAgentId = _agentIds[_selectedIndex];
                                    _logScrollOffset = 0; // Reset scroll when changing agent
                                    _agentsDirty = true;
                                    _logDirty = true;
                                }
                            }
                            else
                            {
                                // Scroll log down (show newer content)
                                _logScrollOffset = Math.Max(0, _logScrollOffset - 5);
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.Home:
                            if (_focus == FocusPanel.Log)
                            {
                                _logScrollOffset = int.MaxValue; // Scroll to top
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.End:
                            if (_focus == FocusPanel.Log)
                            {
                                _logScrollOffset = 0; // Scroll to bottom
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.Q:
                            _cts.Cancel();
                            break;

                        case ConsoleKey.R:
                            if (_selectedAgentId != null && _agentService != null)
                            {
                                var newAgent = _agentService.RestartAgent(_selectedAgentId);
                                if (newAgent != null)
                                {
                                    AddStatusInternal($"[#e5c07b]Restarted {newAgent.Name}[/]");
                                    _agentsDirty = true;
                                    _logDirty = true;
                                }
                            }
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

            // Check if any agent has new log data (for spinner animation)
            var livenessChanged = CheckLivenessChanged();
            if (_agentsDirty || livenessChanged || _cachedAgents == null)
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

            // Check for idle agents and auto-restart
            CheckAndRestartIdleAgents();
        }
    }

    private void CheckAndRestartIdleAgents()
    {
        if (_agentService == null) return;

        var now = DateTime.Now;
        foreach (var agentId in _agentIds.ToList()) // ToList to avoid collection modification
        {
            var agent = _registry.Get(agentId);
            if (agent == null || !agent.IsRunning) continue;

            var lastBeat = _lastHeartbeat.GetValueOrDefault(agentId, now);
            var idleTime = now - lastBeat;

            var timeout = agent.Kind == AgentKind.Supervisor ? SupervisorIdleTimeout : WorkerIdleTimeout;

            if (idleTime > timeout)
            {
                AddStatusInternal($"[#e06c75]{agent.Name} idle for {idleTime.TotalSeconds:F0}s, restarting...[/]");
                var newAgent = _agentService.RestartAgent(agentId);
                if (newAgent != null)
                {
                    AddStatusInternal($"[#98c379]Restarted {newAgent.Name}[/]");
                    _agentsDirty = true;
                    _logDirty = true;
                }
            }
        }
    }

    private bool CheckLogChanged()
    {
        // The tailer reads in background, so we always check for updates
        // A more sophisticated approach would track last-seen line count
        return _selectedAgentId != null && _tailers.ContainsKey(_selectedAgentId);
    }

    private bool CheckLivenessChanged()
    {
        // Check if any agent has received new log lines
        foreach (var agentId in _agentIds)
        {
            var tailer = _tailers.GetValueOrDefault(agentId);
            if (tailer == null) continue;

            var currentCount = tailer.TotalLineCount;
            var lastCount = _lastLineCount.GetValueOrDefault(agentId, 0);

            if (currentCount != lastCount)
                return true;
        }
        return false;
    }

    private Panel BuildHeader()
    {
        // Called with _lock held
        var roundText = _totalRounds > 0 ? $"Round [#61afef]{_currentRound}[/]/[#5c6370]{_totalRounds}[/]" : "";
        var timeText = _remainingTime > TimeSpan.Zero ? $"[#e5c07b]{_remainingTime:mm\\:ss}[/] remaining" : "";
        var phaseText = !string.IsNullOrEmpty(_currentPhase) ? $"[#5c6370]│[/] {_currentPhase}" : "";

        var content = $"[bold #61afef]SWARM[/] {roundText} {timeText} {phaseText}";

        return new Panel(new Markup(content))
            .Border(BoxBorder.None)
            .Expand();
    }

    private Panel BuildStatusPanel()
    {
        // Called with _lock held
        var content = _statusMessages.Count > 0
            ? string.Join("\n", _statusMessages)
            : "[#5c6370]No status messages[/]";

        return new Panel(new Markup(content))
            .Header("[bold]Status[/]")
            .BorderColor(new Color(92, 99, 112))
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

            // Check liveness - advance spinner if line count changed
            var tailer = _tailers.GetValueOrDefault(agentId);
            var currentCount = tailer?.TotalLineCount ?? 0;
            var lastCount = _lastLineCount.GetValueOrDefault(agentId, 0);

            if (currentCount != lastCount)
            {
                _lastLineCount[agentId] = currentCount;
                _lastHeartbeat[agentId] = DateTime.Now;
                var frame = _spinnerFrame.GetValueOrDefault(agentId, 0);
                _spinnerFrame[agentId] = (frame + 1) % SpinnerFrames.Length;
            }

            // Use spinner as status icon when running, red circle when stopped
            var statusIcon = agent.IsRunning
                ? $"[#98c379]{SpinnerFrames[_spinnerFrame.GetValueOrDefault(agentId, 0)]}[/]"
                : "[#e06c75]○[/]";

            var isSelected = i == _selectedIndex;
            var kindIcon = agent.Kind == AgentKind.Supervisor ? "[#c678dd]S[/]" : "[#61afef]W[/]";

            var name = isSelected
                ? $"[bold reverse] {kindIcon} {agent.Name} [/]"
                : $" {kindIcon} {agent.Name}";

            table.AddRow(statusIcon, name);
        }

        if (table.Rows.Count == 0)
        {
            table.AddRow(" ", "[#5c6370]No agents running[/]");
        }

        var focusIndicator = _focus == FocusPanel.Agents ? "[#56b6c2]●[/] " : "";
        var borderColor = _focus == FocusPanel.Agents ? new Color(86, 182, 194) : new Color(92, 99, 112);

        return new Panel(table)
            .Header($"{focusIndicator}[bold]Agents[/] [#5c6370](Tab, ↑/↓, r=restart, q)[/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private Panel BuildLogPanel()
    {
        // Called with _lock held
        if (_selectedAgentId == null)
        {
            return new Panel("[#5c6370]Select an agent to view logs[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(new Color(92, 99, 112))
                .Expand();
        }

        var agent = _registry.Get(_selectedAgentId);
        if (agent == null)
        {
            return new Panel("[#5c6370]Agent not found[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(new Color(92, 99, 112))
                .Expand();
        }

        var tailer = _tailers.GetValueOrDefault(_selectedAgentId);
        var content = tailer?.Tail(lines: LogDisplayLines, offset: _logScrollOffset) ?? "[Waiting for log file...]";
        var lineCount = tailer?.TotalLineCount ?? 0;
        var totalLines = tailer?.LineCount ?? 0;

        var statusText = agent.IsRunning ? "[#98c379]Running[/]" : "[#e06c75]Stopped[/]";
        var timeStamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var scrollInfo = _logScrollOffset > 0 ? $" [#d19a66]↑{_logScrollOffset}[/]" : "";
        var focusIndicator = _focus == FocusPanel.Log ? "[#56b6c2]●[/] " : "";

        var headerText = $"{focusIndicator}[bold]{agent.Name}[/] - {statusText} - [#5c6370]{agent.Runtime}[/] - [#5c6370]{timeStamp} ({lineCount}/{totalLines} lines)[/]{scrollInfo}";

        // Use Markup for Codex/Claude (has color tags), Text for others
        IRenderable logContent;
        if (agent.Runtime is AgentRuntime.Codex or AgentRuntime.Claude)
        {
            try
            {
                logContent = new Markup(content);
            }
            catch
            {
                // Malformed markup, fall back to plain text
                logContent = new Text(content);
            }
        }
        else
        {
            logContent = new Text(content);
        }

        var borderColor = _focus == FocusPanel.Log
            ? new Color(86, 182, 194)
            : new Color(92, 99, 112);

        return new Panel(logContent)
            .Header(headerText)
            .BorderColor(borderColor)
            .Expand();
    }

    private static TailerMode GetTailerMode(AgentInfo agent)
    {
        // Supervisors get filtered output (no tool use/results)
        if (agent.Kind == AgentKind.Supervisor && agent.Runtime == AgentRuntime.Claude)
        {
            return TailerMode.ClaudeSupervisor;
        }

        return agent.Runtime switch
        {
            AgentRuntime.Codex => TailerMode.Codex,
            AgentRuntime.Claude => TailerMode.Claude,
            _ => TailerMode.Plain
        };
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

        // Dispose all tailers (stops background tasks, closes streams)
        foreach (var tailer in _tailers.Values)
        {
            tailer.Dispose();
        }

        _cts.Dispose();
    }
}