using System.Text;

namespace Asynkron.Swarm.Prompts;

public static class SupervisorPrompt
{
    public static string Build(List<string> worktreePaths, List<string> workerLogPaths, string repoPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a supervisor agent overseeing multiple worker agents competing to fix issues.");
        sb.AppendLine();
        sb.AppendLine("## Your Tasks");
        sb.AppendLine();
        sb.AppendLine("### Phase 1: Monitor (while workers are running)");
        sb.AppendLine("Periodically tail the worker log files to monitor progress. Report interesting updates.");
        sb.AppendLine();
        sb.AppendLine("### Phase 2: Evaluate (after workers stop)");
        sb.AppendLine("When you see <<worker has been stopped>> in the logs, the workers have been terminated.");
        sb.AppendLine("At this point:");
        sb.AppendLine("1. Visit each worktree and run the build");
        sb.AppendLine("2. Run the tests in each worktree");
        sb.AppendLine("3. Compare results: which worktree has the most tests passing?");
        sb.AppendLine("4. Pick the winner based on test results");
        sb.AppendLine();
        sb.AppendLine("### Phase 3: Merge");
        sb.AppendLine("Once you've picked a winner:");
        sb.AppendLine($"1. Merge the winner's changes back to the main repo at: {repoPath}");
        sb.AppendLine("2. Use git to cherry-pick or merge the commits");
        sb.AppendLine("3. Report which items from the todo were fixed");
        sb.AppendLine();
        sb.AppendLine("## Worker Locations");
        sb.AppendLine();

        for (var i = 0; i < worktreePaths.Count; i++)
        {
            sb.AppendLine($"- Worker {i + 1}: {worktreePaths[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Log Files");
        sb.AppendLine();

        for (var i = 0; i < workerLogPaths.Count; i++)
        {
            sb.AppendLine($"- Worker {i + 1} log: {workerLogPaths[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Main Repository");
        sb.AppendLine();
        sb.AppendLine($"Path: {repoPath}");
        sb.AppendLine();
        sb.AppendLine("Start by tailing the log files to see what the workers are doing.");

        return sb.ToString();
    }
}
