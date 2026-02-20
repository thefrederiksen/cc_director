namespace CcDirector.Core.Sessions;

/// <summary>
/// Represents a persistent CC Director session workspace.
/// Stored as an individual JSON file in the sessions/ folder.
/// Links to a Claude Code session via ClaudeSessionId.
/// </summary>
public class SessionHistoryEntry
{
    /// <summary>Unique workspace ID (matches Session.Id from when it was first created).</summary>
    public Guid Id { get; set; }

    /// <summary>User-defined display name (e.g., "MyTrader - Trading Framework").</summary>
    public string? CustomName { get; set; }

    /// <summary>User-chosen header color (hex string like "#2563EB"). Null means default.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Repository path this session was opened for.</summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>The Claude session ID linked to this workspace. Null if not yet linked.</summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>When this workspace was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this workspace was last actively used (drives sort order in Resume list).</summary>
    public DateTimeOffset LastUsedAt { get; set; }

    /// <summary>Cached first prompt snippet from the Claude session (for search/display).</summary>
    public string? FirstPromptSnippet { get; set; }

    /// <summary>Per-turn AI-generated summaries, ordered by turn number.</summary>
    public List<string>? TurnSummaries { get; set; }
}
