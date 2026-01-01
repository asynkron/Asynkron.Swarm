using System.Text.RegularExpressions;
using Asynkron.Swarm.UI;

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
        var t = Theme.Current;
        var escaped = Content.Replace("[", "[[").Replace("]", "]]");

        return Kind switch
        {
            AgentMessageKind.Say => $"[{t.SayTextColor}]{ApplyMarkdown(escaped)}[/]",
            AgentMessageKind.Do => $"[{t.DoTextColor}]  → {escaped}[/]",
            AgentMessageKind.See => $"[{t.SeeTextColor}]{Truncate(escaped)}[/]",
            _ => escaped
        };
    }

    private static string ApplyMarkdown(string input)
    {
        var t = Theme.Current;

        // Headers: # ## ### → bold header color
        var result = HeaderRegex().Replace(input, m =>
        {
            var level = m.Groups[1].Value.Length;
            var text = m.Groups[2].Value;
            return level switch
            {
                1 => $"[bold {t.MarkdownHeaderColor}]{text}[/]",
                2 => $"[bold {t.MarkdownHeaderColor}]{text}[/]",
                3 => $"[{t.MarkdownHeaderColor}]{text}[/]",
                _ => $"[{t.HeaderTextColor}]{text}[/]"
            };
        });

        // Bold: **text** → bold
        result = BoldRegex().Replace(result, "[bold]$1[/]");

        // Code: `text` → inline code color
        result = CodeRegex().Replace(result, $"[{t.InlineCodeColor}]$1[/]");

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
