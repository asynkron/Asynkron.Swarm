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

    // Restart context
    public int Round { get; init; }
    public int AgentNumber { get; init; }
    public string? TodoFile { get; init; }
    public AgentType AgentType { get; init; }
    public List<string>? WorktreePaths { get; init; }  // For supervisor
    public List<string>? WorkerLogPaths { get; init; } // For supervisor
    public string? RepoPath { get; init; }             // For supervisor
    public string? SharedFilePath { get; init; }       // For worker inter-communication
    public int RestartCount { get; init; }

    public bool IsRunning => !Process.HasExited;
}
