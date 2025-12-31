using System.Globalization;
using Asynkron.Swarm.Agents;
using Asynkron.Swarm.IO;
using Asynkron.Swarm.Models;
using Asynkron.Swarm.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Asynkron.Swarm.UI;

public sealed class SwarmUI : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

    private int _selectedIndex;
    private string? _selectedAgentId;
    private readonly List<string> _agentIds = [];

    // Focus and scroll state
    private enum FocusPanel { Agents, Log }
    private FocusPanel _focus = FocusPanel.Agents;
    private int _logScrollOffset;

    // Display state per agent (now subscribes to agent's own stream)
    private readonly Dictionary<string, AgentDisplayState> _displayStates = new();

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

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

    public SwarmUI(AgentRegistry registry)
    {
        _registry = registry;

        // Initialize with existing agents
        foreach (var agent in _registry.GetAll())
        {
            SetupAgent(agent);
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

    private void SetupAgent(AgentBase agent)
    {
        _agentIds.Add(agent.Id);

        var displayState = new AgentDisplayState();
        _displayStates[agent.Id] = displayState;

        // Subscribe to agent's OnBufferedMessage - only messages the agent considers relevant
        agent.OnBufferedMessage += msg =>
        {
            lock (_lock)
            {
                // Update spinner for visual feedback
                displayState.SpinnerFrame = (displayState.SpinnerFrame + 1) % SpinnerFrames.Length;

                // Format and add to display
                var formatted = AgentMessageFormatter.Format(msg);
                displayState.AddLine(formatted);

                // Mark UI dirty
                _agentsDirty = true;
                if (_selectedAgentId == agent.Id)
                {
                    _logDirty = true;
                }
            }
        };

        // Subscribe to restart events
        agent.OnRestarted += a =>
        {
            lock (_lock)
            {
                AddStatusInternal($"[#e5c07b]{a.Name} restarted (count: {a.RestartCount})[/]");
                _agentsDirty = true;
                if (_selectedAgentId == a.Id)
                {
                    _logDirty = true;
                }
            }
        };
    }

    private void OnAgentAdded(AgentBase agent)
    {
        lock (_lock)
        {
            SetupAgent(agent);
            _agentsDirty = true;

            if (_selectedAgentId == null)
            {
                _selectedAgentId = agent.Id;
                _selectedIndex = 0;
                _logDirty = true;
            }
        }
    }

    private void OnAgentRemoved(AgentBase agent)
    {
        lock (_lock)
        {
            var index = _agentIds.IndexOf(agent.Id);
            if (index < 0)
            {
                return;
            }

            _agentIds.RemoveAt(index);

            _displayStates.Remove(agent.Id);

            _agentsDirty = true;

            if (_selectedAgentId != agent.Id)
            {
                return;
            }

            _selectedIndex = Math.Max(0, Math.Min(_selectedIndex, _agentIds.Count - 1));
            _selectedAgentId = _agentIds.Count > 0 ? _agentIds[_selectedIndex] : null;
            _logDirty = true;
        }
    }

    private void OnAgentStopped(AgentBase agent)
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
                        // Tick all agents (heartbeat check, crash detection, auto-restart)
                        foreach (var agent in _registry.GetAll())
                        {
                            agent.Tick();
                        }

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
                                    _logScrollOffset = 0;
                                    _agentsDirty = true;
                                    _logDirty = true;
                                }
                            }
                            else
                            {
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
                                    _logScrollOffset = 0;
                                    _agentsDirty = true;
                                    _logDirty = true;
                                }
                            }
                            else
                            {
                                _logScrollOffset = Math.Max(0, _logScrollOffset - 5);
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.Home:
                            if (_focus == FocusPanel.Log)
                            {
                                _logScrollOffset = int.MaxValue;
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.End:
                            if (_focus == FocusPanel.Log)
                            {
                                _logScrollOffset = 0;
                                _logDirty = true;
                            }
                            break;

                        case ConsoleKey.Q:
                            _cts.Cancel();
                            break;

                        case ConsoleKey.R:
                            // Manual restart of selected agent
                            if (_selectedAgentId != null)
                            {
                                var agent = _registry.Get(_selectedAgentId);
                                if (agent != null && agent.IsRunning)
                                {
                                    agent.Restart();
                                    AddStatusInternal($"[#e5c07b]Manual restart: {agent.Name}[/]");
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

            if (_logDirty || _cachedLog == null)
            {
                _cachedLog = BuildLogPanel();
                _layout["Main"]["Log"].Update(_cachedLog);
                _logDirty = false;
            }
        }
    }

    private Panel BuildHeader()
    {
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
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Status", c => c.Width(3))
            .AddColumn("Agent");

        for (var i = 0; i < _agentIds.Count; i++)
        {
            var agentId = _agentIds[i];
            var agent = _registry.Get(agentId);
            if (agent == null)
            {
                continue;
            }

            var displayState = _displayStates.GetValueOrDefault(agentId);
            var spinnerFrame = displayState?.SpinnerFrame ?? 0;

            var statusIcon = agent.IsRunning
                ? $"[#98c379]{SpinnerFrames[spinnerFrame]}[/]"
                : "[#e06c75]○[/]";

            var isSelected = i == _selectedIndex;
            var kindIcon = agent is SupervisorAgent ? "[#c678dd]S[/]" : "[#61afef]W[/]";

            var restartInfo = agent.RestartCount > 0 ? $" [#5c6370](r{agent.RestartCount})[/]" : "";
            var name = isSelected
                ? $"[bold reverse] {kindIcon} {agent.Name}{restartInfo} [/]"
                : $" {kindIcon} {agent.Name}{restartInfo}";

            table.AddRow(statusIcon, name);
        }

        if (table.Rows.Count == 0)
        {
            table.AddRow(" ", "[#5c6370]No agents running[/]");
        }

        var focusIndicator = _focus == FocusPanel.Agents ? "[#61afef]●[/] " : "";
        var borderColor = _focus == FocusPanel.Agents ? new Color(97, 175, 239) : new Color(92, 99, 112);

        return new Panel(table)
            .Header($"{focusIndicator}[bold]Agents[/] [#5c6370](Tab, ↑/↓, r=restart, q)[/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private Panel BuildLogPanel()
    {
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

        var displayState = _displayStates.GetValueOrDefault(_selectedAgentId);
        var content = displayState?.GetDisplay(LogDisplayLines, _logScrollOffset) ?? "[Waiting for output...]";
        var lineCount = displayState?.LineCount ?? 0;

        var statusText = agent.IsRunning ? "[#98c379]Running[/]" : "[#e06c75]Stopped[/]";
        var timeStamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var scrollInfo = _logScrollOffset > 0 ? $" [#d19a66]↑{_logScrollOffset}[/]" : "";
        var focusIndicator = _focus == FocusPanel.Log ? "[#61afef]●[/] " : "";

        var headerText = $"{focusIndicator}[bold]{agent.Name}[/] - {statusText} - [#5c6370]{agent.Cli.FileName}[/] - [#5c6370]{timeStamp} ({lineCount} lines)[/]{scrollInfo}";

        IRenderable logContent;
        try
        {
            logContent = new Markup(content);
        }
        catch
        {
            logContent = new Text(content);
        }

        var borderColor = _focus == FocusPanel.Log
            ? new Color(97, 175, 239)
            : new Color(92, 99, 112);

        return new Panel(logContent)
            .Header(headerText)
            .BorderColor(borderColor)
            .Expand();
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
