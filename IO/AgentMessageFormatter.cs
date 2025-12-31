using System.Text.RegularExpressions;

namespace Asynkron.Swarm.IO;

public static partial class AgentMessageFormatter
{
    public static string Format(AgentMessage message)
    {
        var escaped = EscapeMarkup(message.Content);

        return message.Kind switch
        {
            AgentMessageKind.Say => $"[#abb2bf]{ApplyMarkdown(escaped)}[/]",
            AgentMessageKind.Do => $"[#5c6370]{escaped}[/]",
            AgentMessageKind.See => $"[#4b5263]{TruncateResult(escaped)}[/]",
            _ => escaped
        };
    }

    private static string TruncateResult(string content, int maxLines = 5)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines)
        {
            return content;
        }

        return string.Join('\n', lines.Take(maxLines)) + "\n...";
    }

    private static string EscapeMarkup(string input)
    {
        return input.Replace("[", "[[").Replace("]", "]]");
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

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`(.+?)`")]
    private static partial Regex CodeRegex();
}
