using System.Text;

namespace Asynkron.Swarm.IO;

public class AgentDisplayState
{
    private readonly List<string> _displayLines = [];
    private readonly Lock _lock = new();
    private readonly int _maxLines;

    public DateTime LastHeartbeat { get; set; } = DateTime.Now;
    public int SpinnerFrame { get; set; }

    public AgentDisplayState(int maxLines = 500)
    {
        _maxLines = maxLines;
    }

    public void AddLine(string line)
    {
        lock (_lock)
        {
            _displayLines.Add(line);
            while (_displayLines.Count > _maxLines)
            {
                _displayLines.RemoveAt(0);
            }
        }
    }

    public int LineCount
    {
        get { lock (_lock) { return _displayLines.Count; } }
    }

    public string GetDisplay(int lines, int offset = 0)
    {
        lock (_lock)
        {
            if (_displayLines.Count == 0)
            {
                return "[Waiting for output...]";
            }

            var maxOffset = Math.Max(0, _displayLines.Count - lines);
            offset = Math.Clamp(offset, 0, maxOffset);

            var startIndex = Math.Max(0, _displayLines.Count - lines - offset);
            var endIndex = _displayLines.Count - offset;
            var window = _displayLines.Skip(startIndex).Take(endIndex - startIndex);

            var content = string.Join(Environment.NewLine, window);

            return ReformatMarkdownTables(content);
        }
    }

    private static string ReformatMarkdownTables(string content)
    {
        var lines = content.Split(Environment.NewLine);
        var result = new List<string>();
        var tableBuffer = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var isTableLine = trimmed.StartsWith('|') ||
                              (trimmed.StartsWith('[') && trimmed.Contains('|'));

            if (isTableLine && trimmed.Contains('|'))
            {
                tableBuffer.Add(line);
            }
            else
            {
                if (tableBuffer.Count > 0)
                {
                    result.AddRange(FormatTable(tableBuffer));
                    tableBuffer.Clear();
                }
                result.Add(line);
            }
        }

        if (tableBuffer.Count > 0)
        {
            result.AddRange(FormatTable(tableBuffer));
        }

        return string.Join(Environment.NewLine, result);
    }

    private static List<string> FormatTable(List<string> tableLines)
    {
        if (tableLines.Count == 0) return [];

        var rows = new List<List<string>>();
        var separatorIndices = new List<int>();

        for (var i = 0; i < tableLines.Count; i++)
        {
            var line = tableLines[i];
            var pipeStart = line.IndexOf('|');
            var pipeEnd = line.LastIndexOf('|');

            if (pipeStart < 0 || pipeEnd <= pipeStart)
            {
                rows.Add([line]);
                continue;
            }

            var inner = line.Substring(pipeStart + 1, pipeEnd - pipeStart - 1);
            var cells = inner.Split('|').Select(c => c.Trim()).ToList();

            if (cells.All(c => c.All(ch => ch == '-' || ch == ':' || ch == ' ') && c.Length > 0))
            {
                separatorIndices.Add(i);
            }

            rows.Add(cells);
        }

        var colCount = rows.Max(r => r.Count);
        var colWidths = new int[colCount];

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                var cell = row[i];
                if (!cell.All(ch => ch == '-' || ch == ':' || ch == ' '))
                {
                    var plainText = StripMarkup(cell);
                    colWidths[i] = Math.Max(colWidths[i], plainText.Length);
                }
            }
        }

        for (var i = 0; i < colWidths.Length; i++)
        {
            colWidths[i] = Math.Max(colWidths[i], 3);
        }

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
                    sb.Append(new string('─', colWidths[i] + 2));
                }
                else
                {
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
                while (i < input.Length && input[i] != ']') i++;
                i++;
            }
            else
            {
                result.Append(input[i]);
                i++;
            }
        }
        return result.ToString();
    }
}
