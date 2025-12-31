namespace Asynkron.Swarm.Prompts;

public static class WorkerPrompt
{
    public static string Build(string todoFile, int restartCount = 0)
    {
        var basePrompt = $"read {todoFile} and follow the instructions";

        if (restartCount > 0)
        {
            return $"""
                IMPORTANT: You have been restarted (restart #{restartCount}).

                Before continuing, read your previous work:
                1. Check git log to see what commits you made
                2. Check git status to see uncommitted changes
                3. Run the tests to see current state
                4. Continue from where you left off

                Original task: {basePrompt}
                """;
        }

        return basePrompt;
    }
}

