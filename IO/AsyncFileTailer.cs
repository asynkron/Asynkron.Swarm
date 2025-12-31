using System.Text;
using System.Text.RegularExpressions;

namespace Asynkron.Swarm.IO;

public sealed partial class AsyncFileTailer : IDisposable
{
    private readonly string _path;
    private readonly int _maxLines;
    private readonly bool _codexColoring;
    private readonly List<string> _lines = [];
    private readonly Lock _lock = new();

    private FileStream? _stream;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _totalLineCount;
    private string _cachedContent = "";
    private bool _contentDirty = true;

    // State tracking for Codex output coloring
    private enum OutputState { Normal, Thinking, Exec }
    private OutputState _currentState = OutputState.Normal;

    public int TotalLineCount => _totalLineCount;

    public AsyncFileTailer(string path, int maxLines = 50, bool codexColoring = false)
    {
        _path = path;
        _maxLines = maxLines;
        _codexColoring = codexColoring;
    }

    public void Start()
    {
        if (_readTask != null) return;

        _cts = new CancellationTokenSource();
        _readTask = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // Wait for file to exist
        while (!File.Exists(_path) && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested) return;

        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _reader = new StreamReader(_stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);

            if (line != null)
            {
                lock (_lock)
                {
                    // Clean the line, optionally apply Codex coloring
                    var cleanedLine = _codexColoring ? CleanAndColorLine(line) : CleanLine(line);
                    _lines.Add(cleanedLine);
                    _totalLineCount++;
                    _contentDirty = true;

                    while (_lines.Count > _maxLines)
                    {
                        _lines.RemoveAt(0);
                    }
                }
            }
            else
            {
                // No more data, wait a bit
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    public string Tail(int? lines = null)
    {
        lock (_lock)
        {
            if (_lines.Count == 0)
            {
                return _stream == null ? "[Waiting for log file...]" : "[Empty log file]";
            }

            // Only rebuild content if dirty
            if (_contentDirty || lines.HasValue)
            {
                var count = lines ?? _maxLines;
                var tailLines = _lines.TakeLast(count);
                _cachedContent = string.Join(Environment.NewLine, tailLines);

                if (!lines.HasValue)
                {
                    _contentDirty = false;
                }
            }

            return _cachedContent;
        }
    }

    private static string CleanLine(string input)
    {
        // Strip ANSI escape codes
        var stripped = AnsiRegex().Replace(input, "");

        // Remove non-printable characters, replace tabs with spaces
        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            if (c == '\t')
            {
                sb.Append("    "); // 4 spaces
            }
            else if (c >= 32)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private string CleanAndColorLine(string input)
    {
        // Strip ANSI escape codes
        var stripped = AnsiRegex().Replace(input, "");

        // Remove non-printable characters, replace tabs with spaces
        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            if (c == '\t')
            {
                sb.Append("    "); // 4 spaces
            }
            else if (c >= 32)
            {
                // Escape Spectre markup characters
                if (c == '[') sb.Append("[[");
                else if (c == ']') sb.Append("]]");
                else sb.Append(c);
            }
        }

        var cleaned = sb.ToString();
        var trimmed = cleaned.TrimStart();

        // Detect state transitions
        if (trimmed == "thinking")
        {
            _currentState = OutputState.Thinking;
            return "[dim]thinking[/]";
        }
        if (trimmed == "exec")
        {
            _currentState = OutputState.Exec;
            return "[grey]exec[/]";
        }
        if (trimmed.StartsWith("codex") || trimmed.StartsWith("claude"))
        {
            _currentState = OutputState.Normal;
        }

        // Apply color based on current state
        return _currentState switch
        {
            OutputState.Thinking => $"[dim]{cleaned}[/]",
            OutputState.Exec => $"[grey]{cleaned}[/]",
            _ => cleaned
        };
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    public void Dispose()
    {
        _cts?.Cancel();

        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore cancellation exceptions
        }

        _reader?.Dispose();
        _stream?.Dispose();
        _cts?.Dispose();
    }
}
