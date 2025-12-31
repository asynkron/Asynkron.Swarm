using Asynkron.Swarm.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<SwarmCommand>();

app.Configure(config =>
{
    config.SetApplicationName("swarm");
    config.SetApplicationVersion("1.0.0");
});

return await app.RunAsync(args);
