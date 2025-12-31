using Asynkron.Swarm.IO;

namespace Asynkron.Swarm.Cli;

public delegate void AgentMessageHandler(AgentMessage message);

public abstract class AgentCliBase : IDisposable
{
    private AgentMessageStream? _stream;

    public abstract string FileName { get; }
    public abstract bool UseStdin { get; }

    public event AgentMessageHandler? OnMessage;

    public abstract string BuildArguments(string prompt, string? model = null, string? additionalDir = null);

    public void Subscribe(AgentMessageStream stream)
    {
        _stream = stream;
        _stream.OnLine += OnLineReceived;
    }

    public void Unsubscribe()
    {
        if (_stream != null)
        {
            _stream.OnLine -= OnLineReceived;
            _stream = null;
        }
    }

    private void OnLineReceived(string line)
    {
        foreach (var message in Parse(line))
        {
            OnMessage?.Invoke(message);
        }
    }

    protected abstract IEnumerable<AgentMessage> Parse(string line);

    protected static string EscapeForShell(string input)
    {
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public void Dispose()
    {
        Unsubscribe();
        GC.SuppressFinalize(this);
    }
}
