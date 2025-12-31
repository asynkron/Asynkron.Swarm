using System.Text;
using System.Text.RegularExpressions;

namespace Asynkron.Swarm.IO;

public sealed partial class AsyncFileTailer : IDisposable
{
    private readonly string _path;
    private readonly int _maxLines;
    private readonly List<string> _lines = [];
    private readonly Lock _lock = new();

    private FileStream? _stream;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _totalLineCount;

    public int TotalLineCount => _totalLineCount;

    public AsyncFileTailer(string path, int maxLines = 50)
    {
        _path = path;
        _maxLines = maxLines;
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
                    _lines.Add(line);
                    _totalLineCount++;

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

            var count = lines ?? _maxLines;
            var tailLines = _lines.TakeLast(count);
            var cleaned = tailLines.Select(StripAnsiCodes);
            return string.Join(Environment.NewLine, cleaned);
        }
    }

    private static string StripAnsiCodes(string input)
    {
        return AnsiRegex().Replace(input, "");
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
