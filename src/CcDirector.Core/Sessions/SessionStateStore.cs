using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Sessions;

public class PersistedSession
{
    public Guid Id { get; set; }
    public string RepoPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? ClaudeArgs { get; set; }
    public string? CustomName { get; set; }
    public string? CustomColor { get; set; }
    public int EmbeddedProcessId { get; set; }
    public long ConsoleHwnd { get; set; }
    public string? ClaudeSessionId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActivityState ActivityState { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public SessionStateStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CcDirector_v2",
            "sessions.json");
    }

    public void Save(IEnumerable<PersistedSession> sessions)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(sessions.ToList(), JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    public List<PersistedSession> Load()
    {
        if (!File.Exists(FilePath))
            return new List<PersistedSession>();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<PersistedSession>>(json, JsonOptions)
                ?? new List<PersistedSession>();
        }
        catch
        {
            return new List<PersistedSession>();
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // best effort
        }
    }
}
