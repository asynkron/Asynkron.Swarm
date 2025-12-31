namespace Asynkron.Swarm.Services;

public class TodoService
{
    public static async Task<bool> HasRemainingItemsAsync(string repoPath, string todoFileName)
    {
        var todoPath = Path.Combine(repoPath, todoFileName);

        if (!File.Exists(todoPath))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(todoPath);

        // Check if there's any non-whitespace content remaining
        return !string.IsNullOrWhiteSpace(content);
    }
}
