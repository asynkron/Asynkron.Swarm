namespace Asynkron.Swarm.Prompts;

public static class WorkerPrompt
{
    public static string Build(string todoFile)
    {
        return $"read {todoFile} and follow the instructions";
    }
}
