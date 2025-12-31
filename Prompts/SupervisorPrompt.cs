namespace Asynkron.Swarm.Prompts;

public static class SupervisorPrompt
{
    public static string Build(List<string> worktreePaths, List<string> workerLogPaths, string repoPath, int restartCount = 0)
    {
        var restartNote = restartCount > 0
            ? $"""

            IMPORTANT: You have been restarted (restart #{restartCount}).
            Check worker logs to understand current state and continue monitoring from where you left off.

            """
            : "";
        var workers = string.Join("\n", worktreePaths.Select((p, i) => $"- Worker {i + 1}: {p}"));
        var logs = string.Join("\n", workerLogPaths.Select((p, i) => $"- Worker {i + 1} log: {p}"));

        return $"""
            You are a supervisor agent overseeing multiple worker agents competing to fix issues.
            {restartNote}
            IMPORTANT: Do NOT exit until you have completed ALL phases below. This is a long-running task.

            ## Your Tasks

            ### Phase 1: Monitor (while workers are running)

            DO NOT WRITE SCRIPTS. Just run shell commands directly one by one.

            1. For each worker, run these shell commands directly:
               - read the <log_file>, check for interesting information, if the worker has a plan, is writing code, is running tests etc, check if there are any passing or failing tets in the logs.
               - git -C <worktree> log --oneline -3
               - git -C <worktree> status --short
            2. After checking all workers:
                * Write a short summary (look for test pass/fail in logs) use markdown format, headers, bullet points etc.
                * When presenting markdown tables to the user, make sure to preformat those with spaces for padding so the table look visually good for a human.

            3. If all logs contain "<<worker has been stopped>>" → go to Phase 2
            4. wait 5 seconds
            5. Repeat from step 1

            DO NOT:
            - Write Python/bash scripts
            - Read code files
            - Run tests or builds

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

            START NOW: Begin Phase 1 loop immediately. Print status table every 30 seconds.
            """;
    }
}
