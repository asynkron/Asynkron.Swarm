using System.Text;
using System.Text.RegularExpressions;

namespace Asynkron.Swarm.IO;

public delegate void RawLineHandler(string line);

public sealed partial class AgentMessageStream : IDisposable
{
    private readonly string _path;

    private FileStream? _stream;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public event RawLineHandler? OnLine;

    public string Path => _path;

    public AgentMessageStream(string path)
    {
        _path = path;
    }

    public void Start()
    {
        if (_readTask != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _readTask = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!File.Exists(_path) && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Seek to near end to avoid processing entire history
        // Keep last ~64KB for context
        const long tailBytes = 64 * 1024;
        if (_stream.Length > tailBytes)
        {
            _stream.Seek(-tailBytes, SeekOrigin.End);
            // Skip partial first line (we likely landed mid-line)
            var buffer = new byte[4096];
            var bytesRead = _stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var newlinePos = text.IndexOf('\n');
            if (newlinePos >= 0)
            {
                // Position just after the first newline we find
                _stream.Seek(-tailBytes + newlinePos + 1, SeekOrigin.End);
            }
        }

        _reader = new StreamReader(_stream);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);

                if (line != null)
                {
                    var cleaned = CleanLine(line);
                    if (!string.IsNullOrEmpty(cleaned))
                    {
                        OnLine?.Invoke(cleaned);
                    }
                }
                else
                {
                    // Discard buffered data so we can see newly appended content
                    _reader.DiscardBufferedData();
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // On any error, wait and retry
                await Task.Delay(100, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
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
                sb.Append("    ");
            }
            else if (c >= 32)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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
            // Ignore cancellation
        }

        _reader?.Dispose();
        _stream?.Dispose();
        _cts?.Dispose();
    }
}
