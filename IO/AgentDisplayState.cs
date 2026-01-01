namespace Asynkron.Swarm.IO;

public class AgentDisplayState
{
    private readonly List<string> _displayLines = [];
    private readonly Lock _lock = new();
    private long _version;

    public DateTime LastHeartbeat { get; set; } = DateTime.Now;
    public int SpinnerFrame { get; set; }

    /// <summary>
    /// Version counter - increments on each AddLine. Use to detect changes without fetching content.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    public void AddLine(string line)
    {
        lock (_lock)
        {
            _displayLines.Add(line);
            Interlocked.Increment(ref _version);
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
            var count = Math.Min(lines, _displayLines.Count - offset - startIndex);

            // Use GetRange for O(count) instead of Skip which is O(n)
            var text = _displayLines
                .GetRange(startIndex, count)
                .Select(line => line.TrimEnd())
                .ToList();
            
            var content = string.Join(Environment.NewLine, text);

            // Return raw content - table reformatting was causing display issues
            return content;
        }
    }
}
