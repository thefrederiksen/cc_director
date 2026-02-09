using System.Text.Json;

namespace CcDirector.Core.Sessions;

public class RecentSession
{
    public string RepoPath { get; set; } = string.Empty;
    public string? CustomName { get; set; }
    public DateTime LastUsed { get; set; }
}

public class RecentSessionStore
{
    private const int MaxEntries = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<RecentSession> _entries = new();

    public string FilePath { get; }

    public RecentSessionStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CcDirector",
            "recent-sessions.json");
    }

    public void Load()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(FilePath))
            return;

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<RecentSession>>(json, JsonOptions);
            if (loaded != null)
            {
                _entries.Clear();
                _entries.AddRange(loaded);
            }
        }
        catch
        {
            // If the file is corrupt, start fresh
        }
    }

    public void Add(string repoPath, string? customName)
    {
        if (string.IsNullOrWhiteSpace(customName))
            return;

        var normalized = Path.GetFullPath(repoPath).TrimEnd('\\', '/');

        // Remove existing entry with same repo+name so we can re-add at top
        _entries.RemoveAll(e =>
            string.Equals(Path.GetFullPath(e.RepoPath).TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.CustomName, customName, StringComparison.Ordinal));

        _entries.Insert(0, new RecentSession
        {
            RepoPath = normalized,
            CustomName = customName,
            LastUsed = DateTime.UtcNow
        });

        // Trim to max
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        Save();
    }

    public IReadOnlyList<RecentSession> GetRecent() => _entries.AsReadOnly();

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence
        }
    }
}
