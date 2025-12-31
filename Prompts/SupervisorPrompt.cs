using System.Text;

namespace Asynkron.Swarm.Prompts;

public static class SupervisorPrompt
{
    public static string Build(List<string> worktreePaths, List<string> workerLogPaths, string repoPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a supervisor agent overseeing multiple worker agents competing to fix issues.");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Do NOT exit until you have completed ALL phases below. This is a long-running task.");
        sb.AppendLine();
        sb.AppendLine("## Your Tasks");
        sb.AppendLine();
        sb.AppendLine("### Phase 1: Monitor (while workers are running)");
        sb.AppendLine("Continuously monitor the worker log files in a loop:");
        sb.AppendLine("1. Tail each log file to check progress");
        sb.AppendLine("2. Report interesting updates (what each worker is working on)");
        sb.AppendLine("3. Wait 30 seconds");
        sb.AppendLine("4. REPEAT steps 1-3 until you see <<worker has been stopped>> in ALL logs");
        sb.AppendLine();
        sb.AppendLine("DO NOT proceed to Phase 2 until you see <<worker has been stopped>> in the logs.");
        sb.AppendLine();
        sb.AppendLine("### Phase 2: Evaluate (after workers stop)");
        sb.AppendLine("When you see <<worker has been stopped>> in the logs, the workers have been terminated.");
        sb.AppendLine("At this point:");
        sb.AppendLine("1. Visit each worktree and run: dotnet build");
        sb.AppendLine("2. Run the tests in each worktree: dotnet test");
        sb.AppendLine("3. Compare results: which worktree has the most tests passing?");
        sb.AppendLine("4. Pick the winner based on test results");
        sb.AppendLine();
        sb.AppendLine("### Phase 3: Merge");
        sb.AppendLine("Once you've picked a winner:");
        sb.AppendLine($"1. Go to the winner's worktree and get the list of commits");
        sb.AppendLine($"2. Cherry-pick those commits into the main repo at: {repoPath}");
        sb.AppendLine("3. Report which items from the todo were fixed");
        sb.AppendLine();
        sb.AppendLine("Only exit AFTER Phase 3 is complete.");
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
