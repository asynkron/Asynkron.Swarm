using System.Collections.Concurrent;
using Asynkron.Swarm.Agents;
using Asynkron.Swarm.Models;

namespace Asynkron.Swarm.Services;

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentBase> _agents = new();

    public event Action<AgentBase>? AgentAdded;
    public event Action<AgentBase>? AgentRemoved;
    public event Action<AgentBase>? AgentStopped;

    public void Register(AgentBase agent)
    {
        if (_agents.TryAdd(agent.Id, agent))
        {
            AgentAdded?.Invoke(agent);
        }
    }

    public void Unregister(string agentId)
    {
        if (_agents.TryRemove(agentId, out var agent))
        {
            AgentRemoved?.Invoke(agent);
        }
    }

    public AgentBase? Get(string agentId)
    {
        return _agents.GetValueOrDefault(agentId);
    }

    public IReadOnlyList<AgentBase> GetAll()
    {
        return _agents.Values.ToList();
    }

    public IReadOnlyList<AgentBase> GetRunning()
    {
        return _agents.Values.Where(a => a.IsRunning).ToList();
    }

    public IReadOnlyList<WorkerAgent> GetWorkers()
    {
        return _agents.Values.OfType<WorkerAgent>().ToList();
    }

    public IReadOnlyList<SupervisorAgent> GetSupervisors()
    {
        return _agents.Values.OfType<SupervisorAgent>().ToList();
    }

    public void MarkStopped(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            AgentStopped?.Invoke(agent);
        }
    }

    public void Clear()
    {
        foreach (var agent in _agents.Values.ToList())
        {
            Unregister(agent.Id);
        }
    }

    public int Count => _agents.Count;
    public int RunningCount => _agents.Values.Count(a => a.IsRunning);
}
