using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Flat model representing a JSON message from a Claude Code hook relay.
/// Property names match the snake_case JSON keys from Claude hooks.
/// </summary>
public sealed class PipeMessage
{
    // Common fields
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; set; }

    // Notification
    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    // Tool hooks (PreToolUse, PostToolUse, PostToolUseFailure)
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("tool_input")]
    public JsonElement? ToolInput { get; set; }

    [JsonPropertyName("tool_response")]
    public JsonElement? ToolResponse { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // Subagent hooks
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; set; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; set; }

    // Stop / SubagentStop
    [JsonPropertyName("stop_hook_active")]
    public bool? StopHookActive { get; set; }

    // SessionStart
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    // SessionEnd
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    // UserPromptSubmit
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    // TeammateIdle / TaskCompleted
    [JsonPropertyName("teammate_name")]
    public string? TeammateName { get; set; }

    [JsonPropertyName("team_name")]
    public string? TeamName { get; set; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("task_subject")]
    public string? TaskSubject { get; set; }

    [JsonPropertyName("task_description")]
    public string? TaskDescription { get; set; }

    // PreCompact
    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }

    [JsonPropertyName("custom_instructions")]
    public string? CustomInstructions { get; set; }

    /// <summary>Timestamp when the Director received this message.</summary>
    [JsonIgnore]
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
