namespace Asynkron.Swarm.Prompts;

public static class WorkerPrompt
{
    public static string Build(string todoFile, string agentName, string sharedFilePath, int restartCount = 0, bool autopilot = false, string? branchName = null, string? logPath = null)
    {
        var basePrompt = $"run `cat {todoFile}` to read the todo file (use cat/tail, not Read tool - files can be large), then follow the instructions";

        var sharedFileInstructions = $"""

            ## Inter-Agent Communication

            You are part of a multi-agent swarm. To collaborate with other agents, use the shared file for
            EVERY key-finding, such as bugs, why something works or does´t work, how to fix something, passing tests,
             file: **{sharedFilePath}**

            ### Writing to the shared file
            Document any useful findings, test results, or valuable information by APPENDING to this file.
            Always prefix your entries with your agent name. Format:

            ```
            {agentName} says:
            <your finding or information here>

            ```

            Examples of what to document:
            - Important discoveries about the codebase
            - Test results (e.g., "8 out of 10 tests pass")
            - Errors encountered and their solutions
            - Insights that might help other agents
            - Warnings about pitfalls or gotchas

            ### Reading from the shared file
            Periodically read this file to see if other agents have added context that might help you.
            Check for findings from other agents that could inform your work.

            IMPORTANT: When writing, always APPEND to the file - never overwrite existing content.
            """;

        var autopilotInstructions = autopilot && branchName != null ? $"""

            ## Autopilot Mode - GitHub PR Required

            You are running in autopilot mode. When you have completed your work:
            1. Commit all your changes with a descriptive commit message
            2. Create a new branch named: {branchName}
            3. Push the branch to origin: git push origin {branchName}
            4. Create a GitHub PR using: gh pr create --title "<descriptive title>" --body "<summary of changes>"
            5. Exit when done - do not wait for further instructions

            IMPORTANT: You MUST create a GitHub PR before exiting. This is required in autopilot mode.
            """ : "";

        if (restartCount > 0 && logPath != null)
        {
            return $"""
                IMPORTANT: You have been restarted (restart #{restartCount}).

                DO NOT read the todo.md file - you already picked a task before the restart.
                Instead, recover your previous work:

                1. Run `tail -500 {logPath}` to see what you were doing before the restart
                2. Check git log to see what commits you made
                3. Check git status to see uncommitted changes
                4. Continue EXACTLY where you left off - do not start a new task
                {sharedFileInstructions}{autopilotInstructions}
                """;
        }

        return basePrompt + sharedFileInstructions + autopilotInstructions;
    }
}

