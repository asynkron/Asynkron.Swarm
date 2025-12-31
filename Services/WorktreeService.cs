using System.Diagnostics;

namespace Asynkron.Swarm.Services;

public class WorktreeService
{
    private readonly string _baseDir;

    public WorktreeService()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "swarm-worktrees");
        Directory.CreateDirectory(_baseDir);
    }

    public async Task<List<string>> CreateWorktreesAsync(string repoPath, int round, int count)
    {
        var worktrees = new List<string>();
        var absoluteRepoPath = Path.GetFullPath(repoPath);

        // Prune stale worktree entries from previous crashes
        await RunGitAsync(absoluteRepoPath, "worktree prune");

        for (var i = 1; i <= count; i++)
        {
            var worktreeName = $"round{round}-agent{i}";
            var worktreePath = Path.Combine(_baseDir, worktreeName);

            // Remove if exists from previous failed run
            if (Directory.Exists(worktreePath))
            {
                await RemoveWorktreeAsync(absoluteRepoPath, worktreePath);
            }

            // Create worktree with detached HEAD at current commit
            var result = await RunGitAsync(absoluteRepoPath, $"worktree add --detach \"{worktreePath}\" HEAD");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create worktree: {result.Error}");
            }

            worktrees.Add(worktreePath);
        }

        return worktrees;
    }

    public async Task DeleteWorktreesAsync(string repoPath, List<string> worktreePaths)
    {
        var absoluteRepoPath = Path.GetFullPath(repoPath);

        foreach (var worktreePath in worktreePaths)
        {
            if (Directory.Exists(worktreePath))
            {
                await RemoveWorktreeAsync(absoluteRepoPath, worktreePath);
            }
        }
    }

    private async Task RemoveWorktreeAsync(string repoPath, string worktreePath)
    {
        // Force remove the worktree
        await RunGitAsync(repoPath, $"worktree remove \"{worktreePath}\" --force");

        // Clean up directory if still exists
        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitAsync(string workingDir, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output, error);
    }
}
