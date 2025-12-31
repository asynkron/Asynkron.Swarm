namespace Asynkron.Swarm.Models;

public class SwarmOptions
{
    public required int ClaudeWorkers { get; init; }
    public required int CodexWorkers { get; init; }
    public required int CopilotWorkers { get; init; }
    public required int GeminiWorkers { get; init; }
    public required string Repo { get; init; }
    public required string Todo { get; init; }
    public required int Minutes { get; init; }
    public required AgentType SupervisorType { get; init; }
    public int MaxRounds { get; init; } = 10;

    public int TotalWorkers => ClaudeWorkers + CodexWorkers + CopilotWorkers + GeminiWorkers;
}
