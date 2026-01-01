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
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
            return string.IsNullOrWhiteSpace(line) ? [] : [new AgentMessage(AgentMessageKind.Say, line)];

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
                return [];

            var type = typeEl.GetString();

            // Must materialize results before JsonDocument is disposed
            return type switch
            {
                "message" => ParseMessage(root),
                "tool_use" => ParseToolUse(root),
                "tool_result" => ParseToolResult(root),
                "result" => ParseResult(root),
                _ => []
            };
        }
        catch (JsonException)
        {
            // Malformed JSON, treat as plain text
            return [new AgentMessage(AgentMessageKind.Say, line)];
        }
    }

    private static AgentMessage[] ParseMessage(JsonElement root)
    {
        if (root.TryGetProperty("role", out var role) && role.GetString() == "assistant" &&
            root.TryGetProperty("content", out var content))
        {
            var text = content.GetString() ?? "";
            text = TrimTrailingWhitespacePerLine(text);
            if (!string.IsNullOrWhiteSpace(text))
                return [new AgentMessage(AgentMessageKind.Say, text)];
        }
        return [];
    }

    private static string TrimTrailingWhitespacePerLine(string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }
        return string.Join('\n', lines);
    }

    private static AgentMessage[] ParseToolUse(JsonElement root)
    {
        var toolName = root.TryGetProperty("tool_name", out var name) ? name.GetString() : null;
        var toolParams = root.TryGetProperty("parameters", out var p) ? p.ToString() : null;
        var summary = GetToolSummary(toolName, root);
        return [new AgentMessage(AgentMessageKind.Do, summary, toolName, toolParams)];
    }

    private static AgentMessage[] ParseToolResult(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output))
        {
            var outputStr = output.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(outputStr))
                return [new AgentMessage(AgentMessageKind.See, outputStr)];
        }
        return [];
    }

    private static AgentMessage[] ParseResult(JsonElement root)
    {
        if (root.TryGetProperty("status", out var resultStatus))
        {
            var statusStr = resultStatus.GetString();
            if (statusStr == "error" && root.TryGetProperty("error", out var error))
            {
                var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
                return [new AgentMessage(AgentMessageKind.Say, $"[Error: {errorMsg}]")];
            }
        }
        return [];
    }

    private static string GetToolSummary(string? toolName, JsonElement root)
    {
        if (toolName == null) return "Unknown tool";

        if (!root.TryGetProperty("parameters", out var parameters))
            return toolName;

        return toolName switch
        {
            "run_shell_command" when parameters.TryGetProperty("command", out var cmd) =>
                $"$ {cmd.GetString()}",
            "shell" when parameters.TryGetProperty("command", out var cmd) =>
                $"$ {cmd.GetString()}",
            "read_file" when parameters.TryGetProperty("file_path", out var path) =>
                $"read: {path.GetString()}",
            "write_file" when parameters.TryGetProperty("file_path", out var path) =>
                $"write: {path.GetString()}",
            "edit_file" when parameters.TryGetProperty("file_path", out var path) =>
                $"edit: {path.GetString()}",
            "replace" when parameters.TryGetProperty("file_path", out var path) =>
                $"replace: {path.GetString()}",
            "glob" when parameters.TryGetProperty("pattern", out var pattern) =>
                $"glob: {pattern.GetString()}",
            "grep" when parameters.TryGetProperty("pattern", out var pattern) =>
                $"grep: {pattern.GetString()}",
            _ => toolName
        };
    }
}
