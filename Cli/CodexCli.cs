using Asynkron.Swarm.IO;

namespace Asynkron.Swarm.Cli;

public class CodexCli : AgentCliBase
{
    public override string FileName => "codex";
    public override bool UseStdin => false;

    public override string BuildArguments(string prompt, string? model = null, string? additionalDir = null)
    {
        var modelArg = model != null ? $"--model {model} " : "";
        var addDirArg = additionalDir != null ? $"--add-dir \"{additionalDir}\" " : "";
        return $"exec \"{EscapeForShell(prompt)}\" --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox {addDirArg}{modelArg}".TrimEnd();
    }

    protected override IEnumerable<AgentMessage> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        var trimmed = line.Trim();

        // Codex state transitions
        if (trimmed == "thinking")
        {
            yield return new AgentMessage(AgentMessageKind.Say, "[thinking]");
            yield break;
        }

        if (trimmed == "exec")
        {
            yield return new AgentMessage(AgentMessageKind.Do, "[exec]");
            yield break;
        }

        // Default: treat as Say (reasoning output)
        yield return new AgentMessage(AgentMessageKind.Say, line);
    }
}
