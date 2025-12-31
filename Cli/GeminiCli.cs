using System.Text.Json;
using Asynkron.Swarm.IO;

namespace Asynkron.Swarm.Cli;

public class GeminiCli : AgentCliBase
{
    public override string FileName => "gemini";
    public override bool UseStdin => false;

    public override string BuildArguments(string prompt, string? model = null)
    {
        var modelArg = model != null ? $"--model {model} " : "";
        return $"\"{EscapeForShell(prompt)}\" --yolo --output-format stream-json {modelArg}".TrimEnd();
    }

    protected override IEnumerable<AgentMessage> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];

        // Skip non-JSON lines (like "YOLO mode is enabled", "Loaded cached credentials", "[ERROR]...")
        if (!line.TrimStart().StartsWith('{'))
            return [];

        JsonDocument? doc = null;
        List<AgentMessage> results = [];

        try
        {
            doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
                return results;

            var type = typeEl.GetString();

            switch (type)
            {
                case "message" when root.TryGetProperty("role", out var role) && role.GetString() == "assistant":
                    if (root.TryGetProperty("content", out var content))
                    {
                        var text = content.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                            results.Add(new AgentMessage(AgentMessageKind.Say, text));
                    }
                    break;

                case "tool_use":
                    var toolName = root.TryGetProperty("tool_name", out var name) ? name.GetString() : null;
                    var toolParams = root.TryGetProperty("parameters", out var p) ? p.ToString() : null;
                    var summary = GetToolSummary(toolName, root);
                    results.Add(new AgentMessage(AgentMessageKind.Do, summary, toolName, toolParams));
                    break;

                case "tool_result":
                    if (root.TryGetProperty("status", out var status))
                    {
                        var statusStr = status.GetString();
                        results.Add(new AgentMessage(AgentMessageKind.See, $"[{statusStr}]"));
                    }
                    break;

                case "result":
                    if (root.TryGetProperty("status", out var resultStatus))
                    {
                        var statusStr = resultStatus.GetString();
                        if (statusStr == "error" && root.TryGetProperty("error", out var error))
                        {
                            var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
                            results.Add(new AgentMessage(AgentMessageKind.Say, $"[Error: {errorMsg}]"));
                        }
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

    private static string GetToolSummary(string? toolName, JsonElement root)
    {
        if (toolName == null) return "Unknown tool";

        if (!root.TryGetProperty("parameters", out var parameters))
            return toolName;

        return toolName switch
        {
            "shell" when parameters.TryGetProperty("command", out var cmd) =>
                $"$ {cmd.GetString()}",
            "read_file" when parameters.TryGetProperty("path", out var path) =>
                $"read: {path.GetString()}",
            "write_file" when parameters.TryGetProperty("path", out var path) =>
                $"write: {path.GetString()}",
            "edit_file" when parameters.TryGetProperty("path", out var path) =>
                $"edit: {path.GetString()}",
            "glob" when parameters.TryGetProperty("pattern", out var pattern) =>
                $"glob: {pattern.GetString()}",
            "grep" when parameters.TryGetProperty("pattern", out var pattern) =>
                $"grep: {pattern.GetString()}",
            _ => toolName
        };
    }
}
