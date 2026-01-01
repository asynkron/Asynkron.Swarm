using System.Diagnostics;

namespace Asynkron.Swarm.Services;

public static class WorktreeService
{
    public static async Task<List<string>> CreateWorktreesAsync(string repoPath, List<string> worktreePaths)
    {
        var absoluteRepoPath = Path.GetFullPath(repoPath);

        // Prune stale worktree entries from previous crashes
        await RunGitAsync(absoluteRepoPath, "worktree prune");

        foreach (var worktreePath in worktreePaths)
        {
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
        }

        return worktreePaths;
    }

    public static async Task DeleteWorktreesAsync(string repoPath, List<string> worktreePaths)
    {
        var absoluteRepoPath = Path.GetFullPath(repoPath);

        foreach (var worktreePath in worktreePaths.ToList())
        {
            if (Directory.Exists(worktreePath))
            {
                await RemoveWorktreeAsync(absoluteRepoPath, worktreePath);
            }
        }
    }

    private static async Task RemoveWorktreeAsync(string repoPath, string worktreePath)
    {
        // Force remove the worktree
        await RunGitAsync(repoPath, $"worktree remove \"{worktreePath}\" --force");

        // Clean up directory if still exists - be resilient to partial deletions
        try
        {
            if (Directory.Exists(worktreePath))
            {
                Directory.Delete(worktreePath, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone or partially deleted, that's fine
        }
        catch (IOException)
        {
            // Some files may be locked, ignore during cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // Permission issue, ignore during cleanup
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
