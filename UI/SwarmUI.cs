using System.Globalization;
using System.Runtime.InteropServices;
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
    private readonly bool _arenaMode;
    private readonly bool _autopilot;
    private readonly SwarmSession _session;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

    // Selection tracking - items can be: "session", "todo", or agent IDs
    private int _selectedIndex;
    private string? _selectedItemId;
    private readonly List<string> _itemIds = ["session", "todo"]; // Session and todo are always first

    // Focus and scroll state
    private enum FocusPanel { Agents, Log }
    private FocusPanel _focus = FocusPanel.Agents;
    private int _logScrollOffset;

    // Display state per agent (now subscribes to agent's own stream)
    private readonly Dictionary<string, AgentDisplayState> _displayStates = new();

    // Display state for todo file
    private readonly FileDisplayState _todoDisplayState = new();

    // Display state for completed workers (worker number -> display state)
    private readonly Dictionary<int, FileDisplayState> _completedWorkers = new();

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // Status messages
    private readonly List<string> _statusMessages = [];
    private string _currentPhase = "";
    private int _currentRound;
    private int _totalRounds;
    private TimeSpan _remainingTime;

    private const int RefreshMs = 10;
    private const int MaxStatusMessages = 10;

    // Layout structure - recreated on resize
    private Layout _layout = null!;

    // Cached panels - only rebuild when dirty
    private Panel? _cachedHeader;
    private Panel? _cachedStatus;
    private Panel? _cachedAgents;
    private Panel? _cachedLog;
    private long _cachedLogVersion;
    private int _cachedLogScrollOffset;
    private FocusPanel _cachedLogFocus;

    // Dirty flags
    private bool _headerDirty = true;
    private bool _statusDirty = true;
    private bool _agentsDirty = true;
    private bool _logDirty = true;

    // Console size tracking for resize detection
    private int _lastConsoleWidth;
    private int _lastConsoleHeight;
    private volatile bool _resizePending;

    public SwarmUI(AgentRegistry registry, SwarmSession session, bool arenaMode = false, bool autopilot = false)
    {
        _registry = registry;
        _arenaMode = arenaMode;
        _autopilot = autopilot;
        _session = session;

        // Initialize with existing agents
        foreach (var agent in _registry.GetAll())
        {
            SetupAgent(agent);
        }

        // Load todo file
        var todoPath = Path.Combine(session.Options.Repo, session.Options.Todo);
        _todoDisplayState.Load(todoPath);

        // Default selection is the session
        _selectedItemId = "session";
        _selectedIndex = 0;

        _registry.AgentAdded += OnAgentAdded;
        _registry.AgentRemoved += OnAgentRemoved;
        _registry.AgentStopped += OnAgentStopped;

        _layout = CreateLayout();
    }

    private static Layout CreateLayout()
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Main"));

        layout["Main"].SplitColumns(
            new Layout("Left").Size(50),
            new Layout("Log"));

        layout["Main"]["Left"].SplitRows(
            new Layout("Agents"),
            new Layout("Status").Size(14));

        return layout;
    }

    private void SetupAgent(AgentBase agent)
    {
        _itemIds.Add(agent.Id);

        var displayState = new AgentDisplayState();
        _displayStates[agent.Id] = displayState;

        // Subscribe to ALL messages for spinner updates (shows agent is active)
        agent.OnMessage += _ =>
        {
            lock (_lock)
            {
                displayState.SpinnerFrame = (displayState.SpinnerFrame + 1) % SpinnerFrames.Length;
                _agentsDirty = true;
            }
        };

        // Subscribe to buffered messages for display content
        agent.OnBufferedMessage += msg =>
        {
            lock (_lock)
            {
                // msg.Formatted is cached - computed once per message
                displayState.AddLine(msg.Formatted);

                // Mark UI dirty
                if (_selectedItemId == agent.Id)
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
                AddStatusInternal($"[{Theme.Current.WarningColor}]{a.Name} restarted (count: {a.RestartCount})[/]");
                _agentsDirty = true;
                if (_selectedItemId == a.Id)
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
        }
    }

    private void OnAgentRemoved(AgentBase agent)
    {
        lock (_lock)
        {
            var index = _itemIds.IndexOf(agent.Id);
            if (index < 0)
            {
                return;
            }

            _itemIds.RemoveAt(index);

            _displayStates.Remove(agent.Id);

            _agentsDirty = true;

            if (_selectedItemId != agent.Id)
            {
                return;
            }

            _selectedIndex = Math.Max(0, Math.Min(_selectedIndex, _itemIds.Count - 1));
            _selectedItemId = _itemIds.Count > 0 ? _itemIds[_selectedIndex] : null;
            _logDirty = true;
        }
    }

    private void OnAgentStopped(AgentBase agent)
    {
        lock (_lock)
        {
            _agentsDirty = true;
            if (_selectedItemId == agent.Id)
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

    public void AddCompletedWorker(int workerNumber, string logPath)
    {
        lock (_lock)
        {
            var displayState = new FileDisplayState();
            displayState.Load(logPath);
            _completedWorkers[workerNumber] = displayState;

            // Add to item list as "completed:N"
            var itemId = $"completed:{workerNumber}";
            if (!_itemIds.Contains(itemId))
            {
                _itemIds.Add(itemId);
            }
            _agentsDirty = true;
        }
    }

    private void AddStatusInternal(string message)
    {
        _statusMessages.Add($"[{Theme.Current.DimTextColor}]{DateTime.Now:HH:mm:ss}[/] {message}");
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

        // Register for SIGWINCH on Unix systems
        PosixSignalRegistration? sigwinchReg = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sigwinchReg = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ =>
            {
                _resizePending = true;
            });
        }

        try
        {
            // Track initial console size
            _lastConsoleWidth = Console.WindowWidth;
            _lastConsoleHeight = Console.WindowHeight;

            // Outer loop restarts Live context on resize or error
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Create fresh layout for current dimensions
                    _layout = CreateLayout();
                    _headerDirty = true;
                    _statusDirty = true;
                    _agentsDirty = true;
                    _logDirty = true;
                    _cachedHeader = null;
                    _cachedStatus = null;
                    _cachedAgents = null;
                    _cachedLog = null;
                    _cachedLogVersion = -1;

                    UpdateLayoutRegions();
                    AnsiConsole.Clear();

                    var needRestart = false;

                    await AnsiConsole.Live(_layout)
                        .AutoClear(false)
                        .Overflow(VerticalOverflow.Crop)
                        .Cropping(VerticalOverflowCropping.Top)
                        .StartAsync(async ctx =>
                        {
                            while (!token.IsCancellationRequested && !needRestart)
                            {
                                try
                                {
                                    // Detect console resize
                                    var currentWidth = Console.WindowWidth;
                                    var currentHeight = Console.WindowHeight;
                                    if (_resizePending || currentWidth != _lastConsoleWidth || currentHeight != _lastConsoleHeight)
                                    {
                                        _resizePending = false;
                                        _lastConsoleWidth = currentWidth;
                                        _lastConsoleHeight = currentHeight;

                                        // Break out to restart Live context with new dimensions
                                        needRestart = true;
                                        return;
                                    }

                                    // Tick all agents (crash detection, auto-restart)
                                    foreach (var agent in _registry.GetAll())
                                    {
                                        try { agent.Tick(); } catch { /* ignore tick errors */ }
                                    }

                                    UpdateLayoutRegions();
                                    ctx.UpdateTarget(_layout);
                                    ctx.Refresh();
                                }
                                catch
                                {
                                    // Frame failed - trigger restart to recover
                                    needRestart = true;
                                    return;
                                }

                                await Task.Delay(RefreshMs, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                            }
                        });
                }
                catch
                {
                    // Live context failed - wait briefly and retry
                    await Task.Delay(100, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
        finally
        {
            sigwinchReg?.Dispose();
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
                                    _selectedItemId = _itemIds[_selectedIndex];
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
                                if (_selectedIndex < _itemIds.Count - 1)
                                {
                                    _selectedIndex++;
                                    _selectedItemId = _itemIds[_selectedIndex];
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
                            // Manual restart of selected agent (only if an agent is selected)
                            if (_selectedItemId != null && _selectedItemId != "session" && _selectedItemId != "todo")
                            {
                                var agent = _registry.Get(_selectedItemId);
                                if (agent != null && agent.IsRunning)
                                {
                                    agent.Restart();
                                    AddStatusInternal($"[{Theme.Current.WarningColor}]Manual restart: {agent.Name}[/]");
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
        // Gather state under lock, but do expensive work outside
        bool needHeader, needAgents, needStatus, needLog;
        string? selectedItemId;
        AgentDisplayState? displayState;
        int logScrollOffset;
        FocusPanel focus;

        lock (_lock)
        {
            needHeader = _headerDirty || _cachedHeader == null;
            needAgents = _agentsDirty || _cachedAgents == null;
            needStatus = _statusDirty || _cachedStatus == null;
            needLog = _logDirty || _cachedLog == null;

            selectedItemId = _selectedItemId;
            // Only get display state for agents, not session/todo
            displayState = selectedItemId != null && selectedItemId != "session" && selectedItemId != "todo"
                ? _displayStates.GetValueOrDefault(selectedItemId)
                : null;
            logScrollOffset = _logScrollOffset;
            focus = _focus;

            _headerDirty = false;
            _statusDirty = false;
            _agentsDirty = false;
            _logDirty = false;
        }

        // Get selected agent (null if session or todo is selected)
        var selectedAgent = selectedItemId != null && selectedItemId != "session" && selectedItemId != "todo"
            ? _registry.Get(selectedItemId)
            : null;

        // Build panels outside the lock - each wrapped to prevent cascade failures
        if (needHeader)
        {
            try { _cachedHeader = BuildHeader(); }
            catch { /* keep previous cached value */ }
        }

        if (needAgents)
        {
            try { _cachedAgents = BuildAgentList(); }
            catch { /* keep previous cached value */ }
        }

        if (needStatus)
        {
            try { _cachedStatus = BuildStatusPanel(); }
            catch { /* keep previous cached value */ }
        }

        if (needLog)
        {
            try
            {
                // Only rebuild if version, scroll, or focus actually changed
                var version = displayState?.Version ?? 0;
                if (version != _cachedLogVersion || logScrollOffset != _cachedLogScrollOffset || focus != _cachedLogFocus || _cachedLog == null)
                {
                    _cachedLogVersion = version;
                    _cachedLogScrollOffset = logScrollOffset;
                    _cachedLogFocus = focus;

                    // Calculate available lines based on terminal height
                    // Layout: Header (3) + Main area, Log panel has 2 lines for borders
                    var availableLines = Math.Max(5, _lastConsoleHeight - 3 - 2);
                    var content = "" + displayState?.GetDisplay(availableLines, logScrollOffset);
                    
                    _cachedLog = BuildLogPanelWithContent(selectedItemId, selectedAgent, displayState, content, logScrollOffset, focus);
                }
            }
            catch { /* keep previous cached value */ }
        }

        // Update layout - wrap each to prevent partial failures
        try
        {
            if (_cachedHeader != null)
                _layout["Header"].Update(_cachedHeader);
            if (_cachedAgents != null)
                _layout["Main"]["Left"]["Agents"].Update(_cachedAgents);
            if (_cachedStatus != null)
                _layout["Main"]["Left"]["Status"].Update(_cachedStatus);
            if (_cachedLog != null)
                _layout["Main"]["Log"].Update(_cachedLog);
        }
        catch
        {
            // Layout update failed - will recover on next frame or resize
        }
    }

    private Panel BuildHeader()
    {
        var t = Theme.Current;
        var sessionText = $"[{t.DimTextColor}]{_session.SessionId}[/]";
        var modeText = _autopilot ? $"[{t.AccentTextColor}]Autopilot[/]" : _arenaMode ? $"[{t.AccentTextColor}]Arena[/]" : "";
        var roundText = _totalRounds > 0 ? $"Round [{t.HeaderTextColor}]{_currentRound}[/]/[{t.DimTextColor}]{_totalRounds}[/]" : "";
        var timeText = _remainingTime > TimeSpan.Zero ? $"[{t.AccentTextColor}]{_remainingTime:mm\\:ss}[/] remaining" : "";
        var phaseText = !string.IsNullOrEmpty(_currentPhase) ? $"[{t.DimTextColor}]│[/] {_currentPhase}" : "";

        var content = $"[bold {t.HeaderTextColor}]SWARM[/] {sessionText} {modeText} {roundText} {timeText} {phaseText}";

        return new Panel(new Markup(content))
            .Border(BoxBorder.None)
            .Expand();
    }

    private Panel BuildStatusPanel()
    {
        var t = Theme.Current;
        var content = _statusMessages.Count > 0
            ? string.Join("\n", _statusMessages)
            : $"[{t.DimTextColor}]No status messages[/]";

        return new Panel(new Markup(content))
            .Header("[bold]Status[/]")
            .BorderColor(ParseColor(t.BorderColor))
            .Expand();
    }

    private static Color ParseColor(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return new Color((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }

    private Panel BuildAgentList()
    {
        var t = Theme.Current;
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Tree", c => c.Width(1))
            .AddColumn("Status", c => c.Width(1))
            .AddColumn("Item");

        for (var i = 0; i < _itemIds.Count; i++)
        {
            var itemId = _itemIds[i];
            var isSelected = i == _selectedIndex;

            if (itemId == "session")
            {
                // Session row - parent of the tree
                var sessionName = isSelected
                    ? $"[bold reverse] {_session.SessionId} [/]"
                    : $" [{t.AccentTextColor}]{_session.SessionId}[/]";
                table.AddRow($"[{t.DimTextColor}]─[/]", " ", sessionName);
            }
            else if (itemId == "todo")
            {
                // Todo file row - child of session
                var todoName = isSelected
                    ? $"[bold reverse] {_session.Options.Todo} [/]"
                    : $" [{t.DimTextColor}]{_session.Options.Todo}[/]";
                table.AddRow($"[{t.DimTextColor}]│[/]", " ", todoName);
            }
            else if (itemId.StartsWith("completed:"))
            {
                // Completed worker row - show as file item with checkmark
                var workerNumber = int.Parse(itemId.Split(':')[1]);
                var name = isSelected
                    ? $"[bold reverse] Worker {workerNumber} [/][{t.DimTextColor}](completed)[/]"
                    : $" [{t.DimTextColor}]Worker {workerNumber}[/] [{t.DimTextColor}](completed)[/]";
                table.AddRow($"[{t.DimTextColor}]│[/]", $"[{t.SuccessColor}]✓[/]", name);
            }
            else
            {
                // Agent row - child of session
                var agent = _registry.Get(itemId);
                if (agent == null) continue;

                var displayState = _displayStates.GetValueOrDefault(itemId);
                var spinnerFrame = displayState?.SpinnerFrame ?? 0;

                var statusIcon = agent.IsRunning
                    ? $"[{t.SuccessColor}]{SpinnerFrames[spinnerFrame]}[/]"
                    : $"[{t.ErrorColor}]○[/]";

                var restartInfo = agent.RestartCount > 0 ? $" [{t.DimTextColor}](r{agent.RestartCount})[/]" : "";
                var cliName = Path.GetFileNameWithoutExtension(agent.Cli.FileName);
                var modelInfo = agent.ModelName != null ? $" [{t.DimTextColor}]{agent.ModelName}[/]" : "";

                var name = isSelected
                    ? $"[bold reverse] {agent.Name} [/][{t.CodeTextColor}]{cliName}[/]{modelInfo}{restartInfo}"
                    : $" {agent.Name} [{t.CodeTextColor}]{cliName}[/]{modelInfo}{restartInfo}";

                table.AddRow($"[{t.DimTextColor}]│[/]", statusIcon, name);
            }
        }

        var focusIndicator = _focus == FocusPanel.Agents ? $"[{t.FocusColor}]●[/] " : "";
        var borderColor = _focus == FocusPanel.Agents ? ParseColor(t.FocusColor) : ParseColor(t.BorderColor);

        return new Panel(table)
            .Header($"{focusIndicator}[bold]Session[/] [{t.DimTextColor}](Tab, ↑/↓, r=restart, q)[/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private Panel BuildLogPanelWithContent(string? selectedItemId, AgentBase? agent, AgentDisplayState? displayState, string content, int scrollOffset, FocusPanel focus)
    {
        var t = Theme.Current;
        var focusIndicator = focus == FocusPanel.Log ? $"[{t.FocusColor}]●[/] " : "";
        var borderColor = focus == FocusPanel.Log ? ParseColor(t.FocusColor) : ParseColor(t.BorderColor);

        // Handle session selection
        if (selectedItemId == "session")
        {
            var sessionInfo = $"""
                [bold]Session ID:[/] {_session.SessionId}
                [bold]Path:[/] {_session.SessionPath}
                [bold]Repository:[/] {_session.Options.Repo}
                [bold]Todo:[/] {_session.Options.Todo}
                [bold]Created:[/] {_session.CreatedAt:yyyy-MM-dd HH:mm:ss}

                [bold]Workers:[/] {_session.Options.ClaudeWorkers} Claude, {_session.Options.CodexWorkers} Codex, {_session.Options.CopilotWorkers} Copilot, {_session.Options.GeminiWorkers} Gemini
                [bold]Supervisor:[/] {_session.Options.SupervisorType}
                [bold]Mode:[/] {(_session.Options.Autopilot ? "Autopilot" : _session.Options.Arena ? "Arena" : "Standard")}

                [{t.DimTextColor}]Use --resume {_session.SessionId} to resume this session[/]
                """;

            return new Panel(new Markup(sessionInfo))
                .Header($"{focusIndicator}[bold]Session Info[/]")
                .BorderColor(borderColor)
                .Expand();
        }

        // Handle todo selection - show file content with scroll support
        if (selectedItemId == "todo")
        {
            var visibleLines = Math.Max(10, Console.WindowHeight - 10);
            var todoContent = _todoDisplayState.GetDisplay(visibleLines, scrollOffset);
            var todoLineCount = _todoDisplayState.LineCount;
            var todoScrollInfo = scrollOffset > 0 ? $" [{t.CodeTextColor}]↑{scrollOffset}[/]" : "";

            IRenderable todoRenderable;
            try
            {
                todoRenderable = new Markup(Markup.Escape(todoContent)).Overflow(Overflow.Fold);
            }
            catch
            {
                todoRenderable = new Text(todoContent);
            }

            return new Panel(todoRenderable)
                .Header($"{focusIndicator}[bold]{_session.Options.Todo}[/] [{t.DimTextColor}]({todoLineCount} lines)[/]{todoScrollInfo}")
                .BorderColor(borderColor)
                .Expand();
        }

        // Handle completed worker selection - show log file with scroll support
        if (selectedItemId != null && selectedItemId.StartsWith("completed:"))
        {
            var workerNumber = int.Parse(selectedItemId.Split(':')[1]);
            if (_completedWorkers.TryGetValue(workerNumber, out var completedState))
            {
                var visibleLines = Math.Max(10, Console.WindowHeight - 10);
                var completedLogContent = completedState.GetDisplay(visibleLines, scrollOffset);
                var completedLineCount = completedState.LineCount;
                var completedScrollInfo = scrollOffset > 0 ? $" [{t.CodeTextColor}]↑{scrollOffset}[/]" : "";

                IRenderable completedRenderable;
                try
                {
                    completedRenderable = new Markup(Markup.Escape(completedLogContent)).Overflow(Overflow.Fold);
                }
                catch
                {
                    completedRenderable = new Text(completedLogContent);
                }

                return new Panel(completedRenderable)
                    .Header($"{focusIndicator}[bold]Worker {workerNumber}[/] - [{t.SuccessColor}]Completed[/] [{t.DimTextColor}]({completedLineCount} lines)[/]{completedScrollInfo}")
                    .BorderColor(borderColor)
                    .Expand();
            }
        }

        // Handle no selection
        if (agent == null)
        {
            return new Panel($"[{t.DimTextColor}]Select an item to view details[/]")
                .Header("[bold]Log Output[/]")
                .BorderColor(ParseColor(t.BorderColor))
                .Expand();
        }

        // Handle agent selection - show log
        var lineCount = displayState?.LineCount ?? 0;
        var statusText = agent.IsRunning ? $"[{t.SuccessColor}]Running[/]" : $"[{t.ErrorColor}]Stopped[/]";
        var timeStamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var scrollInfo = scrollOffset > 0 ? $" [{t.CodeTextColor}]↑{scrollOffset}[/]" : "";

        var headerText = $"{focusIndicator}[bold]{agent.Name}[/] - {statusText} - [{t.DimTextColor}]{agent.Cli.FileName}[/] - [{t.DimTextColor}]{timeStamp} ({lineCount} lines)[/]{scrollInfo}";

        IRenderable logContent;
        try
        {
            logContent = new Markup(content).Overflow(Overflow.Fold);
        }
        catch
        {
            logContent = new Text(content);
        }

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
