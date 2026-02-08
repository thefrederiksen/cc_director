using System.Text;
using CcDirector.Core.ConPty;
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
/// Represents a single claude.exe session with its ConPTY, process, and buffer.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly PseudoConsole _console;
    private readonly ProcessHost _processHost;
    private bool _disposed;

    public Guid Id { get; }
    public string RepoPath { get; }
    public string WorkingDirectory { get; }
    public SessionStatus Status { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public string? ClaudeArgs { get; }
    public CircularTerminalBuffer Buffer { get; }
    public int? ExitCode { get; internal set; }
    public int ProcessId => _processHost.ProcessId;

    /// <summary>Claude's cognitive activity state, driven by hook events.</summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.Starting;

    /// <summary>The session_id reported by Claude hooks, used for routing.</summary>
    public string? ClaudeSessionId { get; internal set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    internal ProcessHost ProcessHost => _processHost;

    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        PseudoConsole console,
        ProcessHost processHost,
        CircularTerminalBuffer buffer)
    {
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _console = console;
        _processHost = processHost;
        Buffer = buffer;
        CreatedAt = DateTimeOffset.UtcNow;
        Status = SessionStatus.Starting;
    }

    /// <summary>Send raw bytes to the ConPTY input pipe.</summary>
    public void SendInput(byte[] data)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        _processHost.Write(data);
        SetActivityState(ActivityState.Working);
    }

    /// <summary>Send text to the ConPTY. Appends CR (not LF) as ConPTY expects carriage return.</summary>
    public void SendText(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        var bytes = Encoding.UTF8.GetBytes(text + "\r");
        _processHost.Write(bytes);
        SetActivityState(ActivityState.Working);
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

    /// <summary>Resize the pseudo console.</summary>
    public void Resize(short cols, short rows)
    {
        if (_disposed) return;
        _console.Resize(cols, rows);
    }

    /// <summary>Kill the session gracefully, then force if needed.</summary>
    public async Task KillAsync(int timeoutMs = 5000)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        Status = SessionStatus.Exiting;
        await _processHost.GracefulShutdownAsync(timeoutMs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processHost.Dispose();
        Buffer.Dispose();
    }
}
