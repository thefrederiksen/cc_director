namespace CcDirector.Core.Claude;

/// <summary>
/// Metadata about a Claude Code session, read from sessions-index.json.
/// </summary>
public sealed class ClaudeSessionMetadata
{
    /// <summary>The Claude session ID (UUID).</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>AI-generated summary of the conversation.</summary>
    public string? Summary { get; set; }

    /// <summary>The first prompt (may be truncated).</summary>
    public string? FirstPrompt { get; set; }

    /// <summary>Number of messages in the conversation.</summary>
    public int MessageCount { get; set; }

    /// <summary>When the session was created.</summary>
    public DateTime Created { get; set; }

    /// <summary>When the session was last modified.</summary>
    public DateTime Modified { get; set; }

    /// <summary>The git branch when the session started.</summary>
    public string? GitBranch { get; set; }

    /// <summary>Original project path for this session.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>Full path to the JSONL transcript file.</summary>
    public string? FullPath { get; set; }

    /// <summary>Whether this is a sidechain/subagent session.</summary>
    public bool IsSidechain { get; set; }
}
