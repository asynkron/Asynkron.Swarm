using System.Text.Json;
using Asynkron.Swarm.IO;

namespace Asynkron.Swarm.Cli;

public class ClaudeCli : AgentCliBase
{
    public override string FileName => "claude";
    public override bool UseStdin => true;

    public override string BuildArguments(string prompt, string? model = null)
    {
        var modelArg = model != null ? $"--model {model} " : "";
        return $"-p --dangerously-skip-permissions --tools default --output-format stream-json --verbose {modelArg}".TrimEnd();
    }

    protected override IEnumerable<AgentMessage> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        JsonDocument? doc = null;
        List<AgentMessage> results = [];

        try
        {
            doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
            {
                return results;
            }

            var type = typeEl.GetString();

            switch (type)
            {
                case "assistant":
                    results.AddRange(ParseAssistant(root));
                    break;

                case "user":
                    results.AddRange(ParseToolResult(root));
                    break;

                case "result":
                    if (root.TryGetProperty("result", out var result))
                    {
                        results.Add(new AgentMessage(AgentMessageKind.Say, result.GetString() ?? ""));
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // Not JSON - treat as plain Say
            results.Add(new AgentMessage(AgentMessageKind.Say, line));
        }
        finally
        {
            doc?.Dispose();
        }

        return results;
    }

    private static IEnumerable<AgentMessage> ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg))
        {
            yield break;
        }

        if (!msg.TryGetProperty("content", out var content))
        {
            yield break;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType))
            {
                continue;
            }

            var itemTypeStr = itemType.GetString();

            if (itemTypeStr == "text")
            {
                if (item.TryGetProperty("text", out var text))
                {
                    var textContent = text.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(textContent))
                    {
                        yield return new AgentMessage(AgentMessageKind.Say, textContent);
                    }
                }
            }
            else if (itemTypeStr == "tool_use")
            {
                var toolName = item.TryGetProperty("name", out var name) ? name.GetString() : null;
                var toolInput = item.TryGetProperty("input", out var input) ? input.ToString() : null;

                var summary = GetToolSummary(toolName, item);
                yield return new AgentMessage(AgentMessageKind.Do, summary, toolName, toolInput);
            }
        }
    }

    private static string GetToolSummary(string? toolName, JsonElement item)
    {
        if (toolName == null)
        {
            return "Unknown tool";
        }

        if (!item.TryGetProperty("input", out var input))
        {
            return toolName;
        }

        return toolName switch
        {
            "Bash" when input.TryGetProperty("command", out var cmd) =>
                $"$ {cmd.GetString()}",
            "Read" when input.TryGetProperty("file_path", out var path) =>
                $"read: {path.GetString()}",
            "Write" when input.TryGetProperty("file_path", out var path) =>
                $"write: {path.GetString()}",
            "Edit" when input.TryGetProperty("file_path", out var path) =>
                $"edit: {path.GetString()}",
            "Glob" when input.TryGetProperty("pattern", out var pattern) =>
                $"glob: {pattern.GetString()}",
            "Grep" when input.TryGetProperty("pattern", out var pattern) =>
                $"grep: {pattern.GetString()}",
            _ => toolName
        };
    }

    private static IEnumerable<AgentMessage> ParseToolResult(JsonElement root)
    {
        if (!root.TryGetProperty("tool_use_result", out var result))
        {
            yield break;
        }

        if (result.TryGetProperty("stdout", out var stdout))
        {
            var output = stdout.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(output))
            {
                yield return new AgentMessage(AgentMessageKind.See, output);
            }
        }

        if (result.TryGetProperty("stderr", out var stderr))
        {
            var output = stderr.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(output))
            {
                yield return new AgentMessage(AgentMessageKind.See, output);
            }
        }
    }
}
