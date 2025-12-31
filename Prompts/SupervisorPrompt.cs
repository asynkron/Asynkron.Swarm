namespace Asynkron.Swarm.Prompts;

public static class SupervisorPrompt
{
    public static string Build(List<string> worktreePaths, List<string> workerLogPaths, string repoPath)
    {
        var workers = string.Join("\n", worktreePaths.Select((p, i) => $"- Worker {i + 1}: {p}"));
        var logs = string.Join("\n", workerLogPaths.Select((p, i) => $"- Worker {i + 1} log: {p}"));

        return $"""
            You are a supervisor agent overseeing multiple worker agents competing to fix issues.

            IMPORTANT: Do NOT exit until you have completed ALL phases below. This is a long-running task.

            ## Your Tasks

            ### Phase 1: Monitor (while workers are running)
            Continuously monitor the worker log files in a loop:
            1. Tail each log file to check progress
            2. Report interesting updates (what each worker is working on)
            3. Wait 30 seconds
            4. REPEAT steps 1-3 until you see <<worker has been stopped>> in ALL logs

            DO NOT proceed to Phase 2 until you see <<worker has been stopped>> in the logs.

            ### Phase 2: Evaluate (after workers stop)
            When you see <<worker has been stopped>> in the logs, the workers have been terminated.
            At this point:
            1. Visit each worktree and run: dotnet build
            2. Run the tests in each worktree: dotnet test
            3. Compare results: which worktree has the most tests passing?
            4. Pick the winner based on test results

            ### Phase 3: Merge
            Once you've picked a winner:
            1. Go to the winner's worktree and get the list of commits
            2. Cherry-pick those commits into the main repo at: {repoPath}
            3. Report which items from the todo were fixed

            Only exit AFTER Phase 3 is complete.

            ## Worker Locations

            {workers}

            ## Log Files

            {logs}

            ## Main Repository

            Path: {repoPath}

            Start by tailing the log files to see what the workers are doing.
            """;
    }
}
