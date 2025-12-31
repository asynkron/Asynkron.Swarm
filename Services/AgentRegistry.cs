using System.Collections.Concurrent;
using Asynkron.Swarm.Models;

namespace Asynkron.Swarm.Services;

public class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentInfo> _agents = new();

    public event Action<AgentInfo>? AgentAdded;
    public event Action<AgentInfo>? AgentRemoved;
    public event Action<AgentInfo>? AgentStopped;

    public void Register(AgentInfo agent)
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

    public AgentInfo? Get(string agentId)
    {
        return _agents.GetValueOrDefault(agentId);
    }

    public IReadOnlyList<AgentInfo> GetAll()
    {
        return _agents.Values.ToList();
    }

    public IReadOnlyList<AgentInfo> GetRunning()
    {
        return _agents.Values.Where(a => a.IsRunning).ToList();
    }

    public IReadOnlyList<AgentInfo> GetByKind(AgentKind kind)
    {
        return _agents.Values.Where(a => a.Kind == kind).ToList();
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
