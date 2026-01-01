using System.Text.RegularExpressions;

namespace Asynkron.Swarm.IO;

public enum AgentMessageKind
{
    Say,  // Agent's text output / reasoning
    Do,   // Tool call / exec
    See   // Tool result / response
}

public partial record AgentMessage(
    AgentMessageKind Kind,
    string Content,
    string? ToolName = null,
    string? ToolInput = null
)
{
    private string? _formatted;

    public string Formatted => _formatted ??= Format();

    private string Format()
    {
        var escaped = Content.Replace("[", "[[").Replace("]", "]]");

        return Kind switch
        {
            AgentMessageKind.Say => $"[#abb2bf]{ApplyMarkdown(escaped)}[/]",
            AgentMessageKind.Do => $"[#5c6370]  → {escaped}[/]",
            AgentMessageKind.See => $"[#4b5263]{Truncate(escaped)}[/]",
            _ => escaped
        };
    }

    private static string ApplyMarkdown(string input)
    {
        // Headers: # ## ### → bold cyan
        var result = HeaderRegex().Replace(input, m =>
        {
            var level = m.Groups[1].Value.Length;
            var text = m.Groups[2].Value;
            return level switch
            {
                1 => $"[bold #75c9fa]{text}[/]",
                2 => $"[bold #75c9fa]{text}[/]",
                3 => $"[#75c9fa]{text}[/]",
                _ => $"[#528bbc]{text}[/]"
            };
        });

        // Bold: **text** → bold
        result = BoldRegex().Replace(result, "[bold]$1[/]");

        // Code: `text` → purple
        result = CodeRegex().Replace(result, "[#e19df5]$1[/]");

        return result;
    }

    private static string Truncate(string content, int maxLines = 5)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines)
            return content;
        return string.Join('\n', lines.Take(maxLines)) + "\n...";
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`(.+?)`")]
    private static partial Regex CodeRegex();
}
