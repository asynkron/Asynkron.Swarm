using System.Diagnostics;

namespace Asynkron.Swarm.Models;

public class RoundState
{
    public required int RoundNumber { get; init; }
    public required List<WorkerAgent> Workers { get; init; }
    public required List<string> WorktreePaths { get; init; }
    public required string SupervisorLogPath { get; init; }
    public Process? SupervisorProcess { get; set; }
}
