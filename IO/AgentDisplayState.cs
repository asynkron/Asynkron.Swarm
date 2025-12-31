namespace Asynkron.Swarm.IO;

public class AgentDisplayState
{
    private readonly List<string> _displayLines = [];
    private readonly Lock _lock = new();

    public DateTime LastHeartbeat { get; set; } = DateTime.Now;
    public int SpinnerFrame { get; set; }

    public void AddLine(string line)
    {
        lock (_lock)
        {
            _displayLines.Add(line);
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
            var content = string.Join(Environment.NewLine, _displayLines.GetRange(startIndex, count));

            // Return raw content - table reformatting was causing display issues
            return content;
        }
    }
}
