using System.Diagnostics;
using Asynkron.Swarm.Cli;
using Asynkron.Swarm.IO;

namespace Asynkron.Swarm.Agents;

public abstract class AgentBase : IDisposable
{
    // Immutable configuration for restart
    public string Id { get; }
    public string Name { get; }
    public string LogPath { get; }
    public int Round { get; }
    public int RestartCount { get; private set; }

    // CLI abstraction
    public AgentCliBase Cli { get; }

    // State
    private Process? Process { get; set; }
    private AgentMessageStream? MessageStream { get; set; }
    public DateTimeOffset StartedAt { get; private set; }
    public bool IsRunning => Process is { HasExited: false };

    // Internal message buffer
    private readonly List<AgentMessage> _messages = [];
    private readonly Lock _messagesLock = new();
    public IReadOnlyList<AgentMessage> Messages
    {
        get
        {
            lock (_messagesLock)
            {
                return _messages.ToList();
            }
        }
    }

    protected void AddMessage(AgentMessage message)
    {
        lock (_messagesLock)
        {
            _messages.Add(message);
        }
        OnBufferedMessage?.Invoke(message);
    }

    // Events for external notification
    public event Action<AgentBase>? OnRestarted;
    public event Action<AgentBase>? OnStopped;
    public event AgentMessageHandler? OnMessage;
    public event AgentMessageHandler? OnBufferedMessage;

    // Abstract - different per subtype
    protected abstract void HandleMessage(AgentMessage message);
    protected abstract Process SpawnProcess();

    // Override in subclass to prevent restart on clean exit (e.g., workers)
    protected virtual bool RestartOnCleanExit => true;

    protected AgentBase(string id, string name, AgentCliBase cli, string logPath, int round, int restartCount = 0)
    {
        Id = id;
        Name = name;
        Cli = cli;
        LogPath = logPath;
        Round = round;
        RestartCount = restartCount;

        // Subscribe to CLI messages
        Cli.OnMessage += OnMessageReceived;
    }

    public void Start()
    {
        Process = SpawnProcess();

        // Create stream and have CLI subscribe to it
        MessageStream = new AgentMessageStream(LogPath);
        Cli.Subscribe(MessageStream);
        MessageStream.Start();

        StartedAt = DateTimeOffset.Now;
    }

    public void Stop()
    {
        // Keep MessageStream alive so UI can still read/scroll the log
        KillProcess();
        OnStopped?.Invoke(this);
    }

    private void LogProcessExit(Process process)
    {
        try
        {
            var exitCode = process.ExitCode;
            var exitTime = process.ExitTime;
            var message = $"\n[{DateTime.Now:O}] Process exited with code {exitCode} at {exitTime:O}\n";
            File.AppendAllText(LogPath, message);
        }
        catch
        {
            // Ignore errors reading exit info
        }
    }

    public bool Tick()
    {
        // Check if process has exited
        if (Process is { HasExited: true })
        {
            var exitCode = Process.ExitCode;

            // Only restart if crashed (non-zero exit) or if RestartOnCleanExit is true
            if (exitCode != 0 || RestartOnCleanExit)
            {
                Restart();
                return true;
            }
        }

        return false;
    }

    public void Restart()
    {
        RestartCount++;

        // Append restart marker to log
        var restartMarker = $"\n\n<<agent restarted at {DateTime.Now:O}>>\n\n";
        try
        {
            File.AppendAllText(LogPath, restartMarker);
        }
        catch
        {
            // Ignore write errors
        }

        // Kill old process but keep the stream - it's still reading the same log file
        KillProcess();

        // Spawn new process (stream continues reading)
        Process = SpawnProcess();
        StartedAt = DateTimeOffset.Now;

        OnRestarted?.Invoke(this);
    }

    private void KillProcess()
    {
        if (Process == null) return;

        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
            Process.WaitForExit(2000);
            if (Process.HasExited)
            {
                Process.WaitForExit();
            }
            LogProcessExit(Process);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }

        Process.Dispose();
        Process = null;
    }

    private void OnMessageReceived(AgentMessage msg)
    {
        // Notify external listeners
        OnMessage?.Invoke(msg);

        // Subclass-specific handling
        HandleMessage(msg);
    }

    public void Dispose()
    {
        Cli.OnMessage -= OnMessageReceived;
        Cli.Unsubscribe();

        MessageStream?.Dispose();
        MessageStream = null;

        KillProcess();
        GC.SuppressFinalize(this);
    }

    // Helper for subclasses to start a process with proper logging and output redirection
    protected Process StartProcess(string fileName, string arguments, string workingDir, string? stdinContent = null)
    {
        // Append to log file (for restarts) or create new
        if (RestartCount > 0)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] Agent restarted (count: {RestartCount})\n");
        }
        else
        {
            File.WriteAllText(LogPath, $"[{DateTime.Now:O}] Agent started\n");
        }
        File.AppendAllText(LogPath, $"[{DateTime.Now:O}] Working directory: {workingDir}\n");
        File.AppendAllText(LogPath, $"[{DateTime.Now:O}] Command: {fileName} {arguments}\n");
        File.AppendAllText(LogPath, new string('-', 60) + "\n\n");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinContent != null,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var logLock = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (logLock)
            {
                try
                {
                    File.AppendAllText(LogPath, e.Data + Environment.NewLine);
                }
                catch
                {
                    // Ignore write errors
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (logLock)
            {
                try
                {
                    File.AppendAllText(LogPath, e.Data + Environment.NewLine);
                }
                catch
                {
                    // Ignore write errors
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdinContent == null)
        {
            return process;
        }

        process.StandardInput.Write(stdinContent);
        process.StandardInput.Close();

        return process;
    }
}
