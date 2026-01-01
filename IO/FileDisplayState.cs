namespace Asynkron.Swarm.IO;

public class FileDisplayState
{
    private readonly List<string> _lines = [];
    private long _version;
    private string? _error;

    public long Version => Interlocked.Read(ref _version);
    public int LineCount => _lines.Count;
    public string? Error => _error;

    public void Load(string path)
    {
        _lines.Clear();
        _error = null;

        try
        {
            if (!File.Exists(path))
            {
                _error = $"File not found: {path}";
                return;
            }

            var content = File.ReadAllText(path);
            _lines.AddRange(content.Split('\n'));
            Interlocked.Increment(ref _version);
        }
        catch (Exception ex)
        {
            _error = $"Error reading file: {ex.Message}";
        }
    }

    public string GetDisplay(int lines, int offset = 0)
    {
        if (_error != null)
        {
            return _error;
        }

        if (_lines.Count == 0)
        {
            return "[Empty file]";
        }

        var maxOffset = Math.Max(0, _lines.Count - lines);
        offset = Math.Clamp(offset, 0, maxOffset);

        var startIndex = Math.Max(0, _lines.Count - lines - offset);
        var count = Math.Min(lines, _lines.Count - offset - startIndex);

        var text = _lines.GetRange(startIndex, count);
        return string.Join(Environment.NewLine, text);
    }
}
