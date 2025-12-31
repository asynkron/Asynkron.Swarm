namespace Asynkron.Swarm.Prompts;

public static class WorkerPrompt
{
    public static string Build(string todoFile, string agentName, int restartCount = 0, bool autopilot = false, string? branchName = null, string? logPath = null)
    {
        var basePrompt = $"run `cat {todoFile}` to read the todo file (use cat/tail, not Read tool - files can be large), then follow the instructions";

        var sharedFileInstructions = $"""

            ## Inter-Agent Communication

            You are part of a multi-agent swarm. To collaborate with other agents, use the `tell` command.
            This is a new command that broadcasts messages to all other agents in the swarm.

            ### Using the tell command
            Document ALL relevant findings by using:
            ```
            tell "{agentName}: <your message here>"
            ```

            Examples:
            - `tell "{agentName}: I found a bug in CopycatProxy.cs at lines 2013-2015"`
            - `tell "{agentName}: Tests now pass after fixing the null check in UserService"`
            - `tell "{agentName}: The API endpoint requires authentication - add Bearer token"`
            - `tell "{agentName}: Build fails due to missing dependency - run dotnet restore"`

            What to communicate:
            - Bug locations and descriptions
            - Why something works or doesn't work
            - How to fix specific issues
            - Test results (e.g., "8 out of 10 tests pass")
            - Warnings about pitfalls or gotchas
            - Any insight that might help other agents

            IMPORTANT: Use `tell` frequently to share your findings with the swarm.
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

