using System.Diagnostics;

namespace Asynkron.Swarm.Models;

public class WorkerAgent
{
    public required int AgentNumber { get; init; }
    public required string WorktreePath { get; init; }
    public required string LogFilePath { get; init; }
    public required Process Process { get; init; }
}
