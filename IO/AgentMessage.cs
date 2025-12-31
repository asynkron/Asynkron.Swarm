namespace Asynkron.Swarm.IO;

public enum AgentMessageKind
{
    Say,  // Agent's text output / reasoning
    Do,   // Tool call / exec
    See   // Tool result / response
}

public record AgentMessage(
    AgentMessageKind Kind,
    string Content,
    string? ToolName = null,
    string? ToolInput = null
);
