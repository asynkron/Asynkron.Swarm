using Asynkron.Swarm.IO;

namespace Asynkron.Swarm.Cli;

public class CopilotCli : AgentCliBase
{
    public override string FileName => "copilot";
    public override bool UseStdin => false;

    public override string BuildArguments(string prompt, string? model = null, string? additionalDir = null)
    {
        // Copilot uses --allow-all-paths so additionalDir is not needed
        var modelArg = model ?? "gpt-5";
        return $"-p \"{EscapeForShell(prompt)}\" --allow-all-tools --allow-all-paths --stream on --model {modelArg}";
    }

    protected override IEnumerable<AgentMessage> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        // Default: treat as Say (text output)
        yield return new AgentMessage(AgentMessageKind.Say, line);
    }
}
