using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Memory;
using CcDirector.Core.Pipes;

namespace CcDirector.Core.Sessions;

public enum SessionStatus
{
    Starting,
    Running,
    Exiting,
    Exited,
    Failed
}

/// <summary>
/// Represents a single Claude session. Delegates process management to an ISessionBackend.
/// Session handles metadata, activity state, and routing - backend handles process I/O.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly ISessionBackend _backend;
    private bool _disposed;

    public SessionBackendType BackendType { get; }
    public Guid Id { get; }
    public string RepoPath { get; }
    public string WorkingDirectory { get; }
    public SessionStatus Status { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public string? ClaudeArgs { get; }
    public int? ExitCode { get; internal set; }

    /// <summary>The terminal buffer from the backend. May be null for Embedded mode.</summary>
    public CircularTerminalBuffer? Buffer => _backend.Buffer;

    /// <summary>Process ID from the backend.</summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>Claude's cognitive activity state, driven by hook events.</summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.Starting;

    /// <summary>The session_id reported by Claude hooks, used for routing.</summary>
    public string? ClaudeSessionId { get; internal set; }

    /// <summary>Cached metadata from Claude's sessions-index.json.</summary>
    public ClaudeSessionMetadata? ClaudeMetadata { get; private set; }

    /// <summary>Fires when ClaudeMetadata is refreshed.</summary>
    public event Action<ClaudeSessionMetadata?>? OnClaudeMetadataChanged;

    /// <summary>User-defined display name for this session. Null means use default (repo folder name).</summary>
    public string? CustomName { get; set; }

    /// <summary>User-chosen header color (hex string like "#2563EB"). Null means default dark header.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    /// <summary>Access to the underlying backend for mode-specific operations.</summary>
    public ISessionBackend Backend => _backend;

    /// <summary>
    /// Create a new session with the specified backend.
    /// </summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        ISessionBackend backend,
        SessionBackendType backendType,
        DateTimeOffset? createdAt = null)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _backend = backend;
        BackendType = backendType;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        Status = SessionStatus.Starting;

        // Subscribe to backend events
        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
    }

    /// <summary>
    /// Create a session for restoring a persisted embedded session.
    /// </summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        ISessionBackend backend,
        string? claudeSessionId,
        ActivityState activityState,
        DateTimeOffset createdAt,
        string? customName,
        string? customColor)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _backend = backend;
        BackendType = SessionBackendType.Embedded;
        ClaudeSessionId = claudeSessionId;
        ActivityState = activityState;
        CreatedAt = createdAt;
        CustomName = customName;
        CustomColor = customColor;
        Status = SessionStatus.Running;

        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
    }

    /// <summary>Send raw bytes to the backend.</summary>
    public void SendInput(byte[] data)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        System.Diagnostics.Debug.WriteLine($"[Session.SendInput] {data.Length} bytes");
        _backend.Write(data);
        SetActivityState(ActivityState.Working);
    }

    /// <summary>Send text + Enter to the backend.</summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;

        System.Diagnostics.Debug.WriteLine($"[Session.SendTextAsync] text=\"{text}\" len={text.Length}");
        await _backend.SendTextAsync(text);
        SetActivityState(ActivityState.Working);
    }

    /// <summary>Send text followed by Enter (sync wrapper).</summary>
    public void SendText(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        // Fire and forget for sync API
        _ = SendTextAsync(text);
    }

    /// <summary>Send just an Enter keystroke to the backend.</summary>
    public async Task SendEnterAsync()
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        await _backend.SendEnterAsync();
    }

    /// <summary>Process a hook event and transition activity state accordingly.</summary>
    public void HandlePipeEvent(PipeMessage msg)
    {
        var newState = msg.HookEventName switch
        {
            "Stop" => ActivityState.WaitingForInput,
            "UserPromptSubmit" => ActivityState.Working,
            "PreToolUse" => ActivityState.Working,
            "PostToolUse" => ActivityState.Working,
            "PostToolUseFailure" => ActivityState.Working,
            "PermissionRequest" => ActivityState.WaitingForPerm,
            "Notification" when msg.NotificationType == "permission_prompt" => ActivityState.WaitingForPerm,
            "Notification" => ActivityState.WaitingForInput,
            "SubagentStart" => ActivityState.Working,
            "SubagentStop" => ActivityState.Working,
            "TaskCompleted" => ActivityState.Working,
            "SessionStart" => ActivityState.Idle,
            "SessionEnd" => ActivityState.Exited,
            "TeammateIdle" => (ActivityState?)null,
            "PreCompact" => (ActivityState?)null,
            _ => (ActivityState?)null
        };

        if (!newState.HasValue)
            return;

        // Once we're waiting for user input (green), only explicit user actions
        // or session end can change the state. This prevents late subagent stops
        // from incorrectly turning the indicator blue.
        if (ActivityState == ActivityState.WaitingForInput)
        {
            var allowedFromWaiting = msg.HookEventName is "UserPromptSubmit" or "SessionEnd" or "PermissionRequest"
                || (msg.HookEventName == "Notification" && msg.NotificationType == "permission_prompt");
            if (!allowedFromWaiting)
                return;
        }

        SetActivityState(newState.Value);
    }

    private void SetActivityState(ActivityState newState)
    {
        var old = ActivityState;
        if (old == newState) return;
        ActivityState = newState;
        OnActivityStateChanged?.Invoke(old, newState);
    }

    /// <summary>
    /// Refresh Claude session metadata from sessions-index.json.
    /// Call this after ClaudeSessionId is set or periodically to update message counts.
    /// </summary>
    public void RefreshClaudeMetadata()
    {
        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            if (ClaudeMetadata != null)
            {
                ClaudeMetadata = null;
                OnClaudeMetadataChanged?.Invoke(null);
            }
            return;
        }

        var metadata = ClaudeSessionReader.ReadSessionMetadata(ClaudeSessionId, RepoPath);
        ClaudeMetadata = metadata;
        OnClaudeMetadataChanged?.Invoke(metadata);
    }

    /// <summary>Resize the terminal (only meaningful for ConPty backend).</summary>
    public void Resize(short cols, short rows)
    {
        if (_disposed) return;
        _backend.Resize(cols, rows);
    }

    /// <summary>Kill the session gracefully, then force if needed.</summary>
    public async Task KillAsync(int timeoutMs = 5000)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        Status = SessionStatus.Exiting;
        await _backend.GracefulShutdownAsync(timeoutMs);
    }

    /// <summary>Mark the session as running (called after backend.Start succeeds).</summary>
    internal void MarkRunning()
    {
        Status = SessionStatus.Running;
    }

    /// <summary>Mark the session as failed.</summary>
    internal void MarkFailed()
    {
        Status = SessionStatus.Failed;
    }

    private void OnBackendProcessExited(int exitCode)
    {
        ExitCode = exitCode;
        Status = SessionStatus.Exited;
        HandlePipeEvent(new PipeMessage { HookEventName = "SessionEnd" });
    }

    private void OnBackendStatusChanged(string status)
    {
        System.Diagnostics.Debug.WriteLine($"[Session] Backend status: {status}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _backend.ProcessExited -= OnBackendProcessExited;
        _backend.StatusChanged -= OnBackendStatusChanged;
        _backend.Dispose();
    }
}
