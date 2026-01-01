using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asynkron.Swarm.Models;

public class SwarmSession
{
    private const string SwarmBaseDir = "swarm";

    public string SessionId { get; init; } = null!;
    public string SessionPath { get; init; } = null!;
    public SwarmOptions Options { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }

    // Computed paths
    [JsonIgnore]
    public string ConfigPath => Path.Combine(SessionPath, "session.json");

    public static SwarmSession Create(SwarmOptions options)
    {
        var sessionId = GenerateSessionId();
        var sessionPath = Path.Combine(Path.GetTempPath(), SwarmBaseDir, sessionId);

        Directory.CreateDirectory(sessionPath);

        var session = new SwarmSession
        {
            SessionId = sessionId,
            SessionPath = sessionPath,
            Options = options,
            CreatedAt = DateTimeOffset.Now
        };

        session.Save();
        return session;
    }

    public static SwarmSession? Load(string sessionId)
    {
        var sessionPath = Path.Combine(Path.GetTempPath(), SwarmBaseDir, sessionId);
        var configPath = Path.Combine(sessionPath, "session.json");

        if (!File.Exists(configPath))
            return null;

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<SwarmSession>(json);
    }

    public static IEnumerable<string> ListSessions()
    {
        var basePath = Path.Combine(Path.GetTempPath(), SwarmBaseDir);
        if (!Directory.Exists(basePath))
            yield break;

        foreach (var dir in Directory.GetDirectories(basePath))
        {
            var configPath = Path.Combine(dir, "session.json");
            if (File.Exists(configPath))
                yield return Path.GetFileName(dir);
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, json);
    }

    public string GetWorktreePath(int workerNumber) =>
        Path.Combine(SessionPath, $"wt{workerNumber}");

    public string GetWorkerLogPath(int workerNumber) =>
        Path.Combine(SessionPath, $"worker{workerNumber}.log");

    public string GetSupervisorLogPath() =>
        Path.Combine(SessionPath, "supervisor.log");

    private static string GenerateSessionId()
    {
        // Use timestamp + random bytes for unique, sortable ID
        // No hyphens - easier to double-click and copy
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var randomBytes = RandomNumberGenerator.GetBytes(4);
        var randomHex = Convert.ToHexString(randomBytes).ToLowerInvariant();
        return $"{timestamp}{randomHex}";
    }
}
