using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Asynkron.Swarm.IO;

public enum TailerMode { Plain, Codex, Claude, ClaudeSupervisor }

public sealed partial class AsyncFileTailer : IDisposable
{
    private readonly string _path;
    private readonly int _maxLines;
    private readonly TailerMode _mode;
    private readonly List<string> _lines = [];
    private readonly Lock _lock = new();

    private FileStream? _stream;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _totalLineCount;

    // State tracking for Codex output coloring
    private enum OutputState { Normal, Thinking, Exec }
    private OutputState _currentState = OutputState.Normal;
    private int _execLineCount;

    public int TotalLineCount => _totalLineCount;

    public AsyncFileTailer(string path, int maxLines = 50, TailerMode mode = TailerMode.Plain)
    {
        _path = path;
        _maxLines = maxLines;
        _mode = mode;
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
                    // Process line based on mode
                    var processedLines = _mode switch
                    {
                        TailerMode.Codex => ProcessCodexLine(line),
                        TailerMode.Claude => ProcessClaudeLine(line, showToolOutput: true),
                        TailerMode.ClaudeSupervisor => ProcessClaudeLine(line, showToolOutput: false),
                        _ => [CleanLine(line)]
                    };

                    // Add non-null lines
                    foreach (var processedLine in processedLines)
                    {
                        if (processedLine != null)
                        {
                            _lines.Add(processedLine);

                            while (_lines.Count > _maxLines)
                            {
                                _lines.RemoveAt(0);
                            }
                        }
                    }

                    _totalLineCount++;
                }
            }
            else
            {
                // No more data, wait a bit
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    public string Tail(int? lines = null, int offset = 0)
    {
        lock (_lock)
        {
            if (_lines.Count == 0)
            {
                return _stream == null ? "[Waiting for log file...]" : "[Empty log file]";
            }

            var count = lines ?? _maxLines;

            // Clamp offset to valid range
            var maxOffset = Math.Max(0, _lines.Count - count);
            offset = Math.Clamp(offset, 0, maxOffset);

            // Get window of lines: skip 'offset' lines from the end
            var startIndex = Math.Max(0, _lines.Count - count - offset);
            var endIndex = _lines.Count - offset;
            var windowLines = _lines.Skip(startIndex).Take(endIndex - startIndex);

            var content = string.Join(Environment.NewLine, windowLines);

            // Reformat markdown tables for better readability
            return ReformatMarkdownTables(content);
        }
    }

    public int LineCount
    {
        get { lock (_lock) { return _lines.Count; } }
    }

    private static string ReformatMarkdownTables(string content)
    {
        var lines = content.Split(Environment.NewLine);
        var result = new List<string>();
        var tableBuffer = new List<string>();

        foreach (var line in lines)
        {
            // Check if line is part of a markdown table (starts with |)
            var trimmed = line.TrimStart();
            // Handle lines with Spectre markup - look for | after any markup tags
            var isTableLine = trimmed.StartsWith('|') ||
                              (trimmed.StartsWith('[') && trimmed.Contains('|'));

            if (isTableLine && trimmed.Contains('|'))
            {
                tableBuffer.Add(line);
            }
            else
            {
                // Flush table buffer if we have one
                if (tableBuffer.Count > 0)
                {
                    result.AddRange(FormatTable(tableBuffer));
                    tableBuffer.Clear();
                }
                result.Add(line);
            }
        }

        // Flush remaining table
        if (tableBuffer.Count > 0)
        {
            result.AddRange(FormatTable(tableBuffer));
        }

        return string.Join(Environment.NewLine, result);
    }

    private static List<string> FormatTable(List<string> tableLines)
    {
        if (tableLines.Count == 0) return [];

        // Parse cells from each row
        var rows = new List<List<string>>();
        var separatorIndices = new List<int>();

        for (var i = 0; i < tableLines.Count; i++)
        {
            var line = tableLines[i];

            // Extract the part between first and last |
            var pipeStart = line.IndexOf('|');
            var pipeEnd = line.LastIndexOf('|');

            if (pipeStart < 0 || pipeEnd <= pipeStart)
            {
                rows.Add([line]);
                continue;
            }

            var inner = line.Substring(pipeStart + 1, pipeEnd - pipeStart - 1);
            var cells = inner.Split('|').Select(c => c.Trim()).ToList();

            // Check if this is a separator row (contains only -, :, and spaces)
            if (cells.All(c => c.All(ch => ch == '-' || ch == ':' || ch == ' ') && c.Length > 0))
            {
                separatorIndices.Add(i);
            }

            rows.Add(cells);
        }

        // Calculate max width for each column
        var colCount = rows.Max(r => r.Count);
        var colWidths = new int[colCount];

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                // Don't count separator rows for width calculation
                var cell = row[i];
                if (!cell.All(ch => ch == '-' || ch == ':' || ch == ' '))
                {
                    // Strip markup for width calculation
                    var plainText = StripMarkup(cell);
                    colWidths[i] = Math.Max(colWidths[i], plainText.Length);
                }
            }
        }

        // Ensure minimum width
        for (var i = 0; i < colWidths.Length; i++)
        {
            colWidths[i] = Math.Max(colWidths[i], 3);
        }

        // Format rows
        var result = new List<string>();
        for (var rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            var row = rows[rowIdx];
            var sb = new StringBuilder("│");

            for (var i = 0; i < colCount; i++)
            {
                var cell = i < row.Count ? row[i] : "";

                if (separatorIndices.Contains(rowIdx))
                {
                    // Separator row
                    sb.Append(new string('─', colWidths[i] + 2));
                }
                else
                {
                    // Data row - pad with spaces
                    var plainLen = StripMarkup(cell).Length;
                    var padding = colWidths[i] - plainLen;
                    sb.Append(' ');
                    sb.Append(cell);
                    sb.Append(new string(' ', padding + 1));
                }
                sb.Append('│');
            }

            result.Add(sb.ToString());
        }

        return result;
    }

    private static string StripMarkup(string input)
    {
        // Remove Spectre markup tags like [white], [/], [[, ]]
        var result = new StringBuilder();
        var i = 0;
        while (i < input.Length)
        {
            if (i < input.Length - 1 && input[i] == '[' && input[i + 1] == '[')
            {
                result.Append('[');
                i += 2;
            }
            else if (i < input.Length - 1 && input[i] == ']' && input[i + 1] == ']')
            {
                result.Append(']');
                i += 2;
            }
            else if (input[i] == '[')
            {
                // Skip until closing ]
                while (i < input.Length && input[i] != ']') i++;
                i++; // Skip the ]
            }
            else
            {
                result.Append(input[i]);
                i++;
            }
        }
        return result.ToString();
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

    private string[] ProcessCodexLine(string input) => [CleanAndColorCodexLine(input)];

    private static string[] ProcessClaudeLine(string input, bool showToolOutput)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl)) return [];
            var type = typeEl.GetString();

            return type switch
            {
                "assistant" => ParseAssistantMessage(root, showToolOutput),
                "user" when showToolOutput => ParseToolResult(root),
                "result" => ParseFinalResult(root),
                _ => []
            };
        }
        catch
        {
            // Not valid JSON, return as plain text
            return [EscapeMarkup(input)];
        }
    }

    private static string[] ParseAssistantMessage(JsonElement root, bool showToolOutput)
    {
        var lines = new List<string>();

        if (!root.TryGetProperty("message", out var msg)) return [];
        if (!msg.TryGetProperty("content", out var content)) return [];

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType)) continue;

            if (itemType.GetString() == "text")
            {
                if (item.TryGetProperty("text", out var text))
                {
                    var escaped = EscapeMarkup(text.GetString() ?? "");
                    var formatted = ApplyMarkdown(escaped);
                    lines.Add($"[#abb2bf]{formatted}[/]");
                }
            }
            else if (showToolOutput && itemType.GetString() == "tool_use")
            {
                if (item.TryGetProperty("name", out var name) && item.TryGetProperty("input", out var inputEl))
                {
                    var toolName = name.GetString();
                    if (toolName == "Bash" && inputEl.TryGetProperty("command", out var cmd))
                    {
                        lines.Add($"[#5c6370]$ {EscapeMarkup(cmd.GetString() ?? "")}[/]");
                    }
                    else if (toolName == "Read" && inputEl.TryGetProperty("file_path", out var path))
                    {
                        lines.Add($"[#5c6370]read: {EscapeMarkup(path.GetString() ?? "")}[/]");
                    }
                    else
                    {
                        lines.Add($"[#5c6370]{toolName}[/]");
                    }
                }
            }
        }

        return lines.ToArray();
    }

    private static string[] ParseToolResult(JsonElement root)
    {
        if (!root.TryGetProperty("tool_use_result", out var result)) return [];

        var lines = new List<string>();

        if (result.TryGetProperty("stdout", out var stdout))
        {
            var output = stdout.GetString();
            if (!string.IsNullOrWhiteSpace(output))
            {
                // Limit output lines
                var outputLines = output.Split('\n').Take(5);
                foreach (var line in outputLines)
                {
                    lines.Add($"[#4b5263]{EscapeMarkup(line)}[/]");
                }
                if (output.Split('\n').Length > 5)
                {
                    lines.Add("[#4b5263]...[/]");
                }
            }
        }

        return lines.ToArray();
    }

    private static string[] ParseFinalResult(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
        {
            return [$"[#98c379]{EscapeMarkup(result.GetString() ?? "")}[/]"];
        }
        return [];
    }

    private static string EscapeMarkup(string input)
    {
        return input.Replace("[", "[[").Replace("]", "]]");
    }

    private string CleanAndColorCodexLine(string input)
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
            return "[#abb2bf]thinking[/]";
        }
        if (trimmed == "exec")
        {
            _currentState = OutputState.Exec;
            _execLineCount = 0;
            return "[#5c6370]exec[/]";
        }
        if (trimmed.StartsWith("codex") || trimmed.StartsWith("claude"))
        {
            _currentState = OutputState.Normal;
        }

        // Apply color based on current state
        if (_currentState == OutputState.Exec)
        {
            _execLineCount++;
            if (_execLineCount <= 2)
            {
                return $"[#5c6370]  {cleaned}[/]";
            }
            return null!; // Skip remaining exec lines
        }

        return _currentState switch
        {
            OutputState.Thinking => $"[#abb2bf]{ApplyMarkdown(cleaned)}[/]",
            _ => cleaned
        };
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`(.+?)`")]
    private static partial Regex CodeRegex();

    private static string ApplyMarkdown(string input)
    {
        // Headers: # ## ### → bold cyan
        var result = HeaderRegex().Replace(input, m =>
        {
            var level = m.Groups[1].Value.Length;
            var text = m.Groups[2].Value;
            return level switch
            {
                1 => $"[bold #61afef]{text}[/]",
                2 => $"[bold #61afef]{text}[/]",
                3 => $"[#61afef]{text}[/]",
                _ => $"[#528bbc]{text}[/]"
            };
        });

        // Bold: **text** → bold
        result = BoldRegex().Replace(result, "[bold]$1[/]");

        // Code: `text` → purple (matches supervisor color)
        result = CodeRegex().Replace(result, "[#c678dd]$1[/]");

        return result;
    }

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
