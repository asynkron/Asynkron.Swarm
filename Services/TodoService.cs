using System.Text;

namespace Asynkron.Swarm.Services;

public class TodoService
{
    private const string RivalsHeader = "## Rivals";
    private const string RivalsFooter = "You can steal any work or smart ideas from your competition!";

    public static async Task InjectRivalsAsync(string worktreePath, string todoFileName, List<string> allWorktreePaths)
    {
        var todoPath = Path.Combine(worktreePath, todoFileName);

        if (!File.Exists(todoPath))
        {
            throw new FileNotFoundException($"Todo file not found: {todoPath}");
        }

        var content = await File.ReadAllTextAsync(todoPath);

        // Remove existing rivals section if present
        content = RemoveRivalsSection(content);

        // Build rivals section
        var rivalsSection = BuildRivalsSection(worktreePath, allWorktreePaths);

        // Prepend rivals section
        var newContent = rivalsSection + "\n\n" + content;

        await File.WriteAllTextAsync(todoPath, newContent);
    }

    public static async Task<bool> HasRemainingItemsAsync(string repoPath, string todoFileName)
    {
        var todoPath = Path.Combine(repoPath, todoFileName);

        if (!File.Exists(todoPath))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(todoPath);

        // Remove rivals section for checking
        content = RemoveRivalsSection(content);

        // Check if there's any non-whitespace content remaining
        return !string.IsNullOrWhiteSpace(content);
    }

    private static string BuildRivalsSection(string currentWorktree, List<string> allWorktreePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine(RivalsHeader);

        foreach (var path in allWorktreePaths)
        {
            if (path == currentWorktree) continue;
            
            var name = Path.GetFileName(path);
            sb
                .Append($"- {name}: {path}")
                .AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine(RivalsFooter);

        return sb.ToString();
    }

    private static string RemoveRivalsSection(string content)
    {
        var lines = content.Split('\n').ToList();
        var startIndex = -1;
        var endIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == RivalsHeader)
            {
                startIndex = i;
            }
            else if (startIndex >= 0 && lines[i].Trim() == RivalsFooter)
            {
                endIndex = i;
                break;
            }
        }

        if (startIndex >= 0 && endIndex >= 0)
        {
            // Remove from startIndex to endIndex inclusive, plus any blank lines after
            lines.RemoveRange(startIndex, endIndex - startIndex + 1);

            // Remove leading blank lines
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            {
                lines.RemoveAt(0);
            }
        }

        return string.Join('\n', lines);
    }
}
