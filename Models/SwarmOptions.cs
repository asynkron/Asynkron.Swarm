namespace Asynkron.Swarm.Models;

public class SwarmOptions
{
    public required int Agents { get; init; }
    public required string Repo { get; init; }
    public required string Todo { get; init; }
    public required int Minutes { get; init; }
    public required AgentType AgentType { get; init; }
    public int MaxRounds { get; init; } = 10;
}
