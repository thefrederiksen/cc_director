using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Claude;

/// <summary>
/// Reads Claude Code session metadata from the ~/.claude/projects folder.
/// </summary>
public static class ClaudeSessionReader
{
    private static readonly string ClaudeProjectsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// Convert a repo path to the Claude project folder name.
    /// E.g., "D:\ReposFred\cc_director" -> "D--ReposFred-cc-director"
    /// </summary>
    public static string GetProjectFolder(string repoPath)
    {
        // Claude Code sanitizes paths: replaces : and \ and / with -
        var normalized = Path.GetFullPath(repoPath);
        var sanitized = normalized
            .Replace(":", "-")
            .Replace("\\", "-")
            .Replace("/", "-");
        return sanitized;
    }

    /// <summary>
    /// Get the full path to the Claude projects folder for a repo.
    /// </summary>
    public static string GetProjectFolderPath(string repoPath)
    {
        return Path.Combine(ClaudeProjectsPath, GetProjectFolder(repoPath));
    }

    /// <summary>
    /// Read session metadata from sessions-index.json for a specific session ID.
    /// </summary>
    public static ClaudeSessionMetadata? ReadSessionMetadata(string claudeSessionId, string repoPath)
    {
        var projectFolder = GetProjectFolderPath(repoPath);
        var indexPath = Path.Combine(projectFolder, "sessions-index.json");

        if (!File.Exists(indexPath))
            return null;

        try
        {
            var json = File.ReadAllText(indexPath);
            var index = JsonSerializer.Deserialize<SessionsIndex>(json);

            if (index?.Entries == null)
                return null;

            var entry = index.Entries.FirstOrDefault(e =>
                string.Equals(e.SessionId, claudeSessionId, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                return null;

            return new ClaudeSessionMetadata
            {
                SessionId = entry.SessionId ?? string.Empty,
                Summary = entry.Summary,
                FirstPrompt = entry.FirstPrompt,
                MessageCount = entry.MessageCount,
                Created = ParseIsoDate(entry.Created),
                Modified = ParseIsoDate(entry.Modified),
                GitBranch = entry.GitBranch,
                ProjectPath = entry.ProjectPath,
                FullPath = entry.FullPath,
                IsSidechain = entry.IsSidechain
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClaudeSessionReader] Error reading sessions-index.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read all session metadata for a repo from sessions-index.json.
    /// </summary>
    public static List<ClaudeSessionMetadata> ReadAllSessionMetadata(string repoPath)
    {
        var result = new List<ClaudeSessionMetadata>();
        var projectFolder = GetProjectFolderPath(repoPath);
        var indexPath = Path.Combine(projectFolder, "sessions-index.json");

        if (!File.Exists(indexPath))
            return result;

        try
        {
            var json = File.ReadAllText(indexPath);
            var index = JsonSerializer.Deserialize<SessionsIndex>(json);

            if (index?.Entries == null)
                return result;

            foreach (var entry in index.Entries)
            {
                result.Add(new ClaudeSessionMetadata
                {
                    SessionId = entry.SessionId ?? string.Empty,
                    Summary = entry.Summary,
                    FirstPrompt = entry.FirstPrompt,
                    MessageCount = entry.MessageCount,
                    Created = ParseIsoDate(entry.Created),
                    Modified = ParseIsoDate(entry.Modified),
                    GitBranch = entry.GitBranch,
                    ProjectPath = entry.ProjectPath,
                    FullPath = entry.FullPath,
                    IsSidechain = entry.IsSidechain
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClaudeSessionReader] Error reading sessions-index.json: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Check if a sessions-index.json exists for the given repo.
    /// </summary>
    public static bool HasSessionIndex(string repoPath)
    {
        var projectFolder = GetProjectFolderPath(repoPath);
        var indexPath = Path.Combine(projectFolder, "sessions-index.json");
        return File.Exists(indexPath);
    }

    /// <summary>
    /// Scan all projects in ~/.claude/projects and return all sessions.
    /// Filters out sidechain (subagent) sessions.
    /// Returns sessions sorted by Modified date (most recent first).
    /// </summary>
    public static List<ClaudeSessionMetadata> ScanAllProjects()
    {
        var result = new List<ClaudeSessionMetadata>();

        if (!Directory.Exists(ClaudeProjectsPath))
            return result;

        try
        {
            var projectDirs = Directory.GetDirectories(ClaudeProjectsPath);

            foreach (var projectDir in projectDirs)
            {
                var indexPath = Path.Combine(projectDir, "sessions-index.json");
                if (!File.Exists(indexPath))
                    continue;

                try
                {
                    var json = File.ReadAllText(indexPath);
                    var index = JsonSerializer.Deserialize<SessionsIndex>(json);

                    if (index?.Entries == null)
                        continue;

                    // Get original path from index or derive from folder name
                    var originalPath = index.OriginalPath ?? DerivePathFromFolder(Path.GetFileName(projectDir));

                    foreach (var entry in index.Entries)
                    {
                        // Skip sidechains (subagent sessions)
                        if (entry.IsSidechain)
                            continue;

                        // Skip sessions with no messages
                        if (entry.MessageCount <= 0)
                            continue;

                        result.Add(new ClaudeSessionMetadata
                        {
                            SessionId = entry.SessionId ?? string.Empty,
                            Summary = entry.Summary,
                            FirstPrompt = entry.FirstPrompt,
                            MessageCount = entry.MessageCount,
                            Created = ParseIsoDate(entry.Created),
                            Modified = ParseIsoDate(entry.Modified),
                            GitBranch = entry.GitBranch,
                            ProjectPath = entry.ProjectPath ?? originalPath,
                            FullPath = entry.FullPath,
                            IsSidechain = entry.IsSidechain
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ClaudeSessionReader] Error reading {indexPath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClaudeSessionReader] Error scanning projects: {ex.Message}");
        }

        // Sort by Modified date, most recent first
        return result.OrderByDescending(s => s.Modified).ToList();
    }

    /// <summary>
    /// Derive the original repo path from the sanitized folder name.
    /// E.g., "D--ReposFred-cc-director" -> "D:\ReposFred\cc-director"
    /// This is a best-effort reverse of GetProjectFolder.
    /// </summary>
    private static string DerivePathFromFolder(string folderName)
    {
        if (string.IsNullOrEmpty(folderName))
            return string.Empty;

        // Pattern: First segment is drive letter (e.g., "D-" -> "D:")
        // Remaining dashes are path separators
        var parts = folderName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return folderName;

        // First part is drive letter
        var drive = parts[0] + ":";

        if (parts.Length == 1)
            return drive + "\\";

        // Join remaining parts with backslash
        var path = string.Join("\\", parts.Skip(1));
        return drive + "\\" + path;
    }

    private static DateTime ParseIsoDate(string? isoDate)
    {
        if (string.IsNullOrEmpty(isoDate))
            return DateTime.MinValue;

        if (DateTime.TryParse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        return DateTime.MinValue;
    }

    // Internal JSON models for sessions-index.json
    private sealed class SessionsIndex
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("entries")]
        public List<SessionEntry>? Entries { get; set; }

        [JsonPropertyName("originalPath")]
        public string? OriginalPath { get; set; }
    }

    private sealed class SessionEntry
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("fullPath")]
        public string? FullPath { get; set; }

        [JsonPropertyName("fileMtime")]
        public long FileMtime { get; set; }

        [JsonPropertyName("firstPrompt")]
        public string? FirstPrompt { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("messageCount")]
        public int MessageCount { get; set; }

        [JsonPropertyName("created")]
        public string? Created { get; set; }

        [JsonPropertyName("modified")]
        public string? Modified { get; set; }

        [JsonPropertyName("gitBranch")]
        public string? GitBranch { get; set; }

        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("isSidechain")]
        public bool IsSidechain { get; set; }
    }
}
