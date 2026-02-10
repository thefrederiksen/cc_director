using System.Diagnostics;
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

public enum SessionMode
{
    ConPty,
    PipeMode,
    Embedded
}

/// <summary>
/// Represents a single claude.exe session with its ConPTY or pipe mode process and buffer.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly PseudoConsole? _console;
    private readonly ProcessHost? _processHost;
    private bool _disposed;

    // Pipe mode fields
    private readonly string? _claudePath;
    private readonly string? _defaultClaudeArgs;
    private Process? _currentProcess;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public SessionMode Mode { get; }
    public Guid Id { get; }
    public string RepoPath { get; }
    public string WorkingDirectory { get; }
    public SessionStatus Status { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public string? ClaudeArgs { get; }
    public CircularTerminalBuffer Buffer { get; }
    public int? ExitCode { get; internal set; }

    /// <summary>For embedded mode, set via SetEmbeddedProcessId from the WPF layer.</summary>
    public int EmbeddedProcessId { get; private set; }

    public int ProcessId
    {
        get
        {
            if (Mode == SessionMode.ConPty)
                return _processHost!.ProcessId;
            if (Mode == SessionMode.Embedded)
                return EmbeddedProcessId;
            try { return _currentProcess?.Id ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>Claude's cognitive activity state, driven by hook events.</summary>
    public ActivityState ActivityState { get; private set; } = ActivityState.Starting;

    /// <summary>The session_id reported by Claude hooks, used for routing.</summary>
    public string? ClaudeSessionId { get; internal set; }

    /// <summary>User-defined display name for this session. Null means use default (repo folder name).</summary>
    public string? CustomName { get; set; }

    /// <summary>User-chosen header color (hex string like "#2563EB"). Null means default dark header.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    internal ProcessHost? ProcessHost => _processHost;

    /// <summary>ConPTY mode constructor.</summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        PseudoConsole console,
        ProcessHost processHost,
        CircularTerminalBuffer buffer)
    {
        Mode = SessionMode.ConPty;
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

    /// <summary>Pipe mode constructor — no ConPTY required.</summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        string claudePath,
        string? defaultClaudeArgs,
        CircularTerminalBuffer buffer)
    {
        Mode = SessionMode.PipeMode;
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        _claudePath = claudePath;
        _defaultClaudeArgs = defaultClaudeArgs;
        Buffer = buffer;
        CreatedAt = DateTimeOffset.UtcNow;
        Status = SessionStatus.Running;
        ActivityState = ActivityState.WaitingForInput;
    }

    /// <summary>Embedded mode constructor — console window handles display directly.</summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs)
    {
        Mode = SessionMode.Embedded;
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        Buffer = new CircularTerminalBuffer(1); // minimal; not used in embedded mode
        CreatedAt = DateTimeOffset.UtcNow;
        Status = SessionStatus.Running;
        ActivityState = ActivityState.Starting;
    }

    /// <summary>Reattach constructor — restore a persisted embedded session.</summary>
    internal Session(
        Guid id,
        string repoPath,
        string workingDirectory,
        string? claudeArgs,
        int embeddedProcessId,
        string? claudeSessionId,
        ActivityState activityState,
        DateTimeOffset createdAt,
        string? customName = null,
        string? customColor = null)
    {
        Mode = SessionMode.Embedded;
        Id = id;
        RepoPath = repoPath;
        WorkingDirectory = workingDirectory;
        ClaudeArgs = claudeArgs;
        EmbeddedProcessId = embeddedProcessId;
        ClaudeSessionId = claudeSessionId;
        CustomName = customName;
        CustomColor = customColor;
        Buffer = new CircularTerminalBuffer(1);
        CreatedAt = createdAt;
        Status = SessionStatus.Running;
        ActivityState = activityState;
    }

    /// <summary>Send raw bytes to the ConPTY input pipe.</summary>
    public void SendInput(byte[] data)
    {
        if (Mode is SessionMode.PipeMode or SessionMode.Embedded) return; // no-op outside ConPTY
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        System.Diagnostics.Debug.WriteLine($"[Session.SendInput] {data.Length} bytes");
        _processHost!.Write(data);
        SetActivityState(ActivityState.Working);
    }

    /// <summary>
    /// In ConPTY mode: send text + Enter to the pseudo console.
    /// In pipe mode: spawn claude -p, write prompt to stdin, drain stdout to buffer.
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;

        // Embedded mode: keystrokes are sent via EmbeddedConsoleHost.SendText
        if (Mode == SessionMode.Embedded) return;

        if (Mode == SessionMode.ConPty)
        {
            System.Diagnostics.Debug.WriteLine($"[Session.SendTextAsync] text=\"{text}\" len={text.Length}");
            var textBytes = Encoding.UTF8.GetBytes(text);
            _processHost!.Write(textBytes);
            await Task.Delay(50);
            _processHost.Write([(byte)'\r']);
            System.Diagnostics.Debug.WriteLine($"[Session.SendTextAsync] Done");
            SetActivityState(ActivityState.Working);
            return;
        }

        // Pipe mode
        if (!await _busy.WaitAsync(0))
        {
            System.Diagnostics.Debug.WriteLine("[Session.SendTextAsync] Busy, ignoring prompt");
            return;
        }

        try
        {
            SetActivityState(ActivityState.Working);

            // Echo prompt to buffer for visual feedback
            var echoBytes = Encoding.UTF8.GetBytes($"> {text}\n\n");
            Buffer.Write(echoBytes);

            // Build args: -p [claudeArgs] [--resume sessionId]
            var args = BuildPipeModeArgs();

            // Clear ClaudeSessionId so EventRouter.FindUnmatchedSession can re-map
            // the new Claude session_id to this Director session
            ClaudeSessionId = null;

            var psi = new ProcessStartInfo
            {
                FileName = _claudePath!,
                Arguments = args,
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            System.Diagnostics.Debug.WriteLine($"[Session.PipeMode] Starting: {_claudePath} {args}");

            var process = new Process { StartInfo = psi };
            process.Start();
            _currentProcess = process;

            // Write prompt to stdin and close it (claude -p reads all of stdin)
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();

            // Drain stdout → buffer in background
            var stdoutTask = DrainStreamToBufferAsync(process.StandardOutput.BaseStream);

            // Drain stderr for logging
            var stderrTask = DrainStderrAsync(process.StandardError);

            // Wait for process to exit
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            ExitCode = process.ExitCode;
            System.Diagnostics.Debug.WriteLine($"[Session.PipeMode] Process exited with code {process.ExitCode}");

            // Add separator after response
            Buffer.Write(Encoding.UTF8.GetBytes("\n"));

            _currentProcess = null;
            process.Dispose();

            SetActivityState(ActivityState.WaitingForInput);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Session.PipeMode] Error: {ex.Message}");
            var errorBytes = Encoding.UTF8.GetBytes($"\n[Error: {ex.Message}]\n");
            Buffer.Write(errorBytes);
            _currentProcess = null;
            SetActivityState(ActivityState.WaitingForInput);
        }
        finally
        {
            _busy.Release();
        }
    }

    private string BuildPipeModeArgs()
    {
        var sb = new StringBuilder("-p");

        // Append user's claude args or defaults
        var extraArgs = ClaudeArgs ?? _defaultClaudeArgs ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            sb.Append(' ');
            sb.Append(extraArgs);
        }

        // Resume if we have a previous Claude session id
        if (ClaudeSessionId != null)
        {
            sb.Append(" --resume ");
            sb.Append(ClaudeSessionId);
        }

        return sb.ToString();
    }

    private async Task DrainStreamToBufferAsync(Stream stream)
    {
        var buf = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            Buffer.Write(buf.AsSpan(0, bytesRead));
        }
    }

    private async Task DrainStderrAsync(StreamReader stderr)
    {
        var content = await stderr.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(content))
        {
            System.Diagnostics.Debug.WriteLine($"[Session.PipeMode.stderr] {content}");
        }
    }

    /// <summary>Send text followed by Enter to the ConPTY (sync, legacy).
    /// Prefer SendTextAsync which handles bracketed paste mode correctly.</summary>
    public void SendText(string text)
    {
        if (Mode is SessionMode.PipeMode or SessionMode.Embedded) return; // no-op outside ConPTY
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        var textBytes = Encoding.UTF8.GetBytes(text);
        _processHost!.Write(textBytes);
        _processHost.Write([(byte)'\r']);
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

    /// <summary>Set the process ID for embedded mode sessions.</summary>
    public void SetEmbeddedProcessId(int pid)
    {
        if (Mode != SessionMode.Embedded) return;
        EmbeddedProcessId = pid;
    }

    /// <summary>Notify the session that the embedded process has exited.</summary>
    public void NotifyEmbeddedProcessExited(int exitCode)
    {
        if (Mode != SessionMode.Embedded) return;
        ExitCode = exitCode;
        Status = SessionStatus.Exited;
        HandlePipeEvent(new Pipes.PipeMessage { HookEventName = "SessionEnd" });
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
        if (_disposed || Mode is SessionMode.PipeMode or SessionMode.Embedded) return;
        _console!.Resize(cols, rows);
    }

    /// <summary>Kill the session gracefully, then force if needed.</summary>
    public async Task KillAsync(int timeoutMs = 5000)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        Status = SessionStatus.Exiting;

        if (Mode == SessionMode.ConPty)
        {
            await _processHost!.GracefulShutdownAsync(timeoutMs);
        }
        else if (Mode == SessionMode.PipeMode)
        {
            // Pipe mode: kill current process if running
            try
            {
                if (_currentProcess is { HasExited: false } proc)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Session.KillAsync] Error killing pipe process: {ex.Message}");
            }

            Status = SessionStatus.Exited;
            SetActivityState(ActivityState.Exited);
        }
        else
        {
            // Embedded mode: process is owned by EmbeddedConsoleHost
            Status = SessionStatus.Exited;
            SetActivityState(ActivityState.Exited);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Mode == SessionMode.ConPty)
        {
            _processHost!.Dispose();
        }
        else if (Mode == SessionMode.PipeMode)
        {
            // Kill pipe mode process if still running
            try
            {
                if (_currentProcess is { HasExited: false } proc)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* best effort */ }

            _currentProcess?.Dispose();
            _busy.Dispose();
        }
        // Embedded mode: process cleanup is handled by EmbeddedConsoleHost

        Buffer.Dispose();
    }
}
