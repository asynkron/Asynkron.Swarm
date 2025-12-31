using System.Diagnostics;

namespace Asynkron.Swarm.Models;

public enum AgentKind
{
    Worker,
    Supervisor
}

public enum AgentRuntime
{
    Claude,
    Codex
}

public record AgentInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AgentKind Kind { get; init; }
    public required AgentRuntime Runtime { get; init; }
    public required string LogPath { get; init; }
    public string? WorktreePath { get; init; }
    public required Process Process { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.Now;

    public bool IsRunning => !Process.HasExited;
}
