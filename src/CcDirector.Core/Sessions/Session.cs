using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Memory;
using CcDirector.Core.Pipes;
using CcDirector.Core.Utilities;

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
/// Status of terminal-based verification (matching terminal content to .jsonl files).
/// </summary>
public enum TerminalVerificationStatus
{
    /// <summary>Waiting - no match found yet.</summary>
    Waiting,
    /// <summary>Potential match found but not yet confirmed (< 50 lines).</summary>
    Potential,
    /// <summary>Matched - terminal content confirmed (50+ lines).</summary>
    Matched,
    /// <summary>Failed - could not find a matching .jsonl file after 50+ lines.</summary>
    Failed
}

/// <summary>
/// Result of verifying a session by matching terminal content to .jsonl files.
/// </summary>
public sealed class TerminalVerificationResult
{
    public bool IsMatched { get; init; }
    public bool IsPotential { get; init; }
    public string? MatchedSessionId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Represents a single Claude session. Delegates process management to an ISessionBackend.
/// Session handles metadata, activity state, and routing - backend handles process I/O.
/// </summary>
public sealed class Session : IDisposable
{
    /// <summary>Minimum length of first prompt required for verification (avoid verifying too early).</summary>
    public const int MinVerificationLength = 50;

    private readonly ISessionBackend _backend;
    private readonly TurnAccumulator _turnAccumulator = new();
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

    /// <summary>Status of session file verification (whether .jsonl exists and is readable).</summary>
    public SessionVerificationStatus VerificationStatus { get; private set; } = SessionVerificationStatus.NotLinked;

    /// <summary>The first prompt snippet from the verified .jsonl file.</summary>
    public string? VerifiedFirstPrompt { get; private set; }

    /// <summary>The expected first prompt to verify against (set from persisted state).</summary>
    public string? ExpectedFirstPrompt { get; set; }

    /// <summary>Fires when verification status changes.</summary>
    public event Action<SessionVerificationStatus>? OnVerificationStatusChanged;

    /// <summary>User-defined display name for this session. Null means use default (repo folder name).</summary>
    public string? CustomName { get; set; }

    /// <summary>User-chosen header color (hex string like "#2563EB"). Null means default dark header.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Links this session to a SessionHistoryEntry for persistent workspace tracking.</summary>
    public Guid? HistoryEntryId { get; set; }

    /// <summary>Raw terminal output captured during Claude Code startup. Preserved for future parsing.</summary>
    public string? RawStartupText { get; set; }

    /// <summary>Terminal-based verification status (matching terminal to .jsonl).</summary>
    public TerminalVerificationStatus TerminalVerificationStatus { get; private set; } = TerminalVerificationStatus.Waiting;

    /// <summary>Fires when terminal verification status changes.</summary>
    public event Action<TerminalVerificationStatus>? OnTerminalVerificationStatusChanged;

    /// <summary>Number of confirmation attempts made (at 50+ lines). Allows retries up to a limit.</summary>
    private volatile int _confirmationAttempts;

    /// <summary>Max confirmation attempts before giving up permanently.</summary>
    private const int MaxConfirmationAttempts = 5;

    /// <summary>Guard to prevent concurrent verification runs.</summary>
    private int _verificationRunning;

    /// <summary>
    /// Mark this session as pre-verified (for restored sessions that already have a ClaudeSessionId).
    /// This skips terminal verification since the session was previously verified.
    /// </summary>
    public void MarkAsPreVerified()
    {
        if (!string.IsNullOrEmpty(ClaudeSessionId))
        {
            _confirmationAttempts = MaxConfirmationAttempts;
            SetTerminalVerificationStatus(TerminalVerificationStatus.Matched);
        }
    }

    /// <summary>Prompt text the user was composing but hasn't sent yet. Persisted across switches and restarts.</summary>
    public string? PendingPromptText { get; set; }

    /// <summary>Order in the session list, used to restore UI order after restart.</summary>
    public int SortOrder { get; set; }

    /// <summary>Fires when ActivityState changes. Args: (oldState, newState).</summary>
    public event Action<ActivityState, ActivityState>? OnActivityStateChanged;

    /// <summary>Fires when a turn completes (Stop event received after UserPromptSubmit).</summary>
    public event Action<Session, TurnData>? OnTurnCompleted;

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
        string? customColor,
        string? pendingPromptText = null)
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
        PendingPromptText = pendingPromptText;
        Status = SessionStatus.Running;

        _backend.ProcessExited += OnBackendProcessExited;
        _backend.StatusChanged += OnBackendStatusChanged;
    }

    /// <summary>Send raw bytes to the backend.</summary>
    public void SendInput(byte[] data)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;
        FileLog.Write($"[Session] SendInput: session={Id}, bytes={data.Length}, firstByte=0x{(data.Length > 0 ? data[0].ToString("X2") : "00")}");
        _backend.Write(data);
        SetActivityState(ActivityState.Working);
    }

    /// <summary>Send text + Enter to the backend.</summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || Status is SessionStatus.Exited or SessionStatus.Failed) return;

        FileLog.Write($"[Session] SendTextAsync: session={Id}, text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\", len={text.Length}");
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
        FileLog.Write($"[Session] HandlePipeEvent: session={Id}, event={msg.HookEventName}, tool={msg.ToolName ?? "n/a"}, currentState={ActivityState}");

        // Accumulate turn data for session summary
        if (msg.HookEventName == "UserPromptSubmit" && !string.IsNullOrEmpty(msg.Prompt))
        {
            var interrupted = _turnAccumulator.StartTurn(msg.Prompt);
            if (interrupted != null)
                OnTurnCompleted?.Invoke(this, interrupted);
        }
        else if (msg.HookEventName == "PreToolUse")
            _turnAccumulator.AddToolUse(msg);
        else if (msg.HookEventName == "Stop" && _turnAccumulator.IsActive)
        {
            var turnData = _turnAccumulator.FinishTurn();
            OnTurnCompleted?.Invoke(this, turnData);
        }

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
        {
            FileLog.Write($"[Session] HandlePipeEvent: no state change for event={msg.HookEventName}");
            return;
        }

        // Once we're waiting for user input (green), only explicit user actions
        // or session end can change the state. This prevents late subagent stops
        // from incorrectly turning the indicator blue.
        if (ActivityState == ActivityState.WaitingForInput)
        {
            var allowedFromWaiting = msg.HookEventName is "UserPromptSubmit" or "SessionEnd" or "PermissionRequest"
                || (msg.HookEventName == "Notification" && msg.NotificationType == "permission_prompt");
            if (!allowedFromWaiting)
            {
                FileLog.Write($"[Session] HandlePipeEvent: blocked {msg.HookEventName} while WaitingForInput");
                return;
            }
        }

        FileLog.Write($"[Session] HandlePipeEvent: session={Id}, {ActivityState}->{newState.Value}");
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

    /// <summary>
    /// Verify that the Claude session's .jsonl file exists and matches expected content.
    /// Updates VerificationStatus and VerifiedFirstPrompt.
    /// Uses ExpectedFirstPrompt if set, otherwise just verifies file existence.
    /// Requires at least MinVerificationLength characters to verify.
    /// </summary>
    public void VerifyClaudeSession()
    {
        FileLog.Write($"[Session] VerifyClaudeSession: session={Id}, claudeSessionId={ClaudeSessionId ?? "null"}");
        var oldStatus = VerificationStatus;

        // Can't verify without a session ID
        if (string.IsNullOrEmpty(ClaudeSessionId))
        {
            VerificationStatus = SessionVerificationStatus.NotLinked;
            VerifiedFirstPrompt = null;
            if (oldStatus != VerificationStatus)
                OnVerificationStatusChanged?.Invoke(VerificationStatus);
            return;
        }

        // Read the JSONL first prompt to check length
        var jsonlPath = ClaudeSessionReader.GetJsonlPath(ClaudeSessionId, RepoPath);
        var firstPrompt = ClaudeSessionReader.ReadFirstPromptFromJsonl(jsonlPath);

        // Need minimum content to verify (avoid verifying new sessions too early)
        if (string.IsNullOrEmpty(firstPrompt) || firstPrompt.Length < MinVerificationLength)
        {
            // File exists but not enough content yet - stay NotLinked (no badge)
            VerificationStatus = SessionVerificationStatus.NotLinked;
            VerifiedFirstPrompt = firstPrompt;
            if (oldStatus != VerificationStatus)
                OnVerificationStatusChanged?.Invoke(VerificationStatus);
            return;
        }

        // Now do full verification
        var result = ClaudeSessionReader.VerifySessionFile(ClaudeSessionId, RepoPath, ExpectedFirstPrompt);
        VerificationStatus = result.Status;
        VerifiedFirstPrompt = result.FirstPromptSnippet;

        // If verified and we didn't have an expected prompt yet, save the actual one
        if (result.Status == SessionVerificationStatus.Verified && string.IsNullOrEmpty(ExpectedFirstPrompt))
        {
            ExpectedFirstPrompt = result.FirstPromptSnippet;
        }

        if (oldStatus != result.Status)
        {
            OnVerificationStatusChanged?.Invoke(result.Status);
        }
    }

    /// <summary>
    /// Find the matching .jsonl file by comparing terminal content with user prompts.
    /// Starts matching immediately - shows "Potential" for early matches, "Matched" after 50+ lines.
    /// </summary>
    /// <param name="terminalText">Terminal content.</param>
    /// <param name="lineCount">Number of lines in terminal.</param>
    /// <returns>Verification result with matched session ID or error.</returns>
    public TerminalVerificationResult VerifyWithTerminalContent(string terminalText, int lineCount)
    {
        FileLog.Write($"[Session] VerifyWithTerminalContent: session={Id}, lineCount={lineCount}, textLen={terminalText.Length}");
        // Skip if already matched or exhausted all retry attempts
        if (TerminalVerificationStatus == TerminalVerificationStatus.Matched)
        {
            return new TerminalVerificationResult
            {
                IsMatched = true,
                MatchedSessionId = ClaudeSessionId
            };
        }
        if (_confirmationAttempts >= MaxConfirmationAttempts)
        {
            return new TerminalVerificationResult
            {
                IsMatched = false,
                MatchedSessionId = ClaudeSessionId
            };
        }

        // Prevent concurrent verification runs (called from background threads)
        if (Interlocked.CompareExchange(ref _verificationRunning, 1, 0) != 0)
            return new TerminalVerificationResult { IsMatched = false, ErrorMessage = "Verification already running" };

        try
        {
            return VerifyWithTerminalContentCore(terminalText, lineCount);
        }
        finally
        {
            Interlocked.Exchange(ref _verificationRunning, 0);
        }
    }

    private TerminalVerificationResult VerifyWithTerminalContentCore(string terminalText, int lineCount)
    {
        FileLog.Write($"[Session.Verify] START: lineCount={lineCount}, status={TerminalVerificationStatus}, attempts={_confirmationAttempts}, sessionId={Id}");

        bool isConfirmationRun = lineCount >= 50;
        if (isConfirmationRun)
            _confirmationAttempts++;

        var error = LoadJsonlFilesForVerification(isConfirmationRun, out var allFiles);
        if (error != null) return error;

        // Normalize terminal text once for whitespace-insensitive matching
        var normalizedTerminal = ClaudeSessionReader.NormalizeForMatching(terminalText);

        // Score all files and pick the best match
        var bestMatch = FindBestMatch(allFiles, terminalText, normalizedTerminal);

        if (bestMatch != null)
        {
            ClaudeSessionId = bestMatch.Value.SessionId;

            if (isConfirmationRun || bestMatch.Value.MatchCount >= 2)
            {
                SetTerminalVerificationStatus(TerminalVerificationStatus.Matched);
                ExpectedFirstPrompt = ClaudeSessionReader.ReadFirstPromptFromJsonl(bestMatch.Value.FilePath);
                VerifyClaudeSession();
                FileLog.Write($"[Session.Verify] MATCHED: {bestMatch.Value.SessionId} (matches={bestMatch.Value.MatchCount}, prompts={bestMatch.Value.TotalPrompts})");
                return new TerminalVerificationResult { IsMatched = true, MatchedSessionId = bestMatch.Value.SessionId };
            }

            SetTerminalVerificationStatus(TerminalVerificationStatus.Potential);
            FileLog.Write($"[Session.Verify] POTENTIAL: {bestMatch.Value.SessionId} (matches={bestMatch.Value.MatchCount}, prompts={bestMatch.Value.TotalPrompts})");
            return new TerminalVerificationResult { IsPotential = true, MatchedSessionId = bestMatch.Value.SessionId };
        }

        if (isConfirmationRun)
        {
            // Set Failed status immediately, but allow retries up to MaxConfirmationAttempts
            FileLog.Write($"[Session.Verify] NO MATCH FOUND - Setting status=Failed (attempt {_confirmationAttempts}/{MaxConfirmationAttempts}, {allFiles.Count} files checked)");
            SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
        }
        else
        {
            FileLog.Write($"[Session.Verify] No match found yet, NOT confirmation run - staying in status={TerminalVerificationStatus}");
        }
        return new TerminalVerificationResult { ErrorMessage = "No matching .jsonl file found" };
    }

    /// <summary>
    /// Load and validate .jsonl files for terminal verification.
    /// Returns null on success (files populated), or an error result on failure.
    /// </summary>
    private TerminalVerificationResult? LoadJsonlFilesForVerification(
        bool isConfirmationRun, out List<FileInfo> allFiles)
    {
        allFiles = new List<FileInfo>();

        var projectFolder = ClaudeSessionReader.GetProjectFolderPath(RepoPath);
        if (!Directory.Exists(projectFolder))
        {
            FileLog.Write($"[Session.Verify] Project folder not found: {projectFolder}");
            if (isConfirmationRun)
                SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
            return new TerminalVerificationResult { ErrorMessage = "Project folder not found" };
        }

        allFiles = Directory.GetFiles(projectFolder, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        FileLog.Write($"[Session.Verify] Found {allFiles.Count} .jsonl files in {projectFolder}");

        if (allFiles.Count == 0)
        {
            if (isConfirmationRun)
                SetTerminalVerificationStatus(TerminalVerificationStatus.Failed);
            else
                FileLog.Write($"[Session.Verify] No .jsonl files, NOT confirmation run - staying in current status");
            return new TerminalVerificationResult { ErrorMessage = "No .jsonl files found" };
        }

        return null;
    }

    private readonly record struct MatchResult(string SessionId, string FilePath, int MatchCount, int TotalPrompts);

    /// <summary>
    /// Score all JSONL files against terminal text and return the best match.
    /// Uses both exact and whitespace-normalized matching to handle word wrapping.
    /// Picks the file with the most prompt matches (not ratio), requiring at least 1 match.
    /// </summary>
    private MatchResult? FindBestMatch(
        IReadOnlyList<FileInfo> allFiles, string terminalText, string normalizedTerminal)
    {
        MatchResult? best = null;

        foreach (var file in allFiles)
        {
            var prompts = ClaudeSessionReader.ExtractUserPrompts(file.FullName);
            if (prompts.Count == 0) continue;

            int matchCount = 0;
            foreach (var prompt in prompts)
            {
                // Try exact match first (fast)
                if (terminalText.Contains(prompt, StringComparison.Ordinal))
                {
                    matchCount++;
                    continue;
                }

                // Try whitespace-normalized match (handles word wrapping)
                var normalizedPrompt = ClaudeSessionReader.NormalizeForMatching(prompt);
                if (normalizedPrompt.Length > 10 && normalizedTerminal.Contains(normalizedPrompt, StringComparison.Ordinal))
                {
                    matchCount++;
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(file.Name);
            var shortName = fileName.Length > 8 ? fileName[..8] : fileName;
            FileLog.Write($"[Session.Verify] File={shortName}..., prompts={prompts.Count}, matched={matchCount}");

            if (matchCount > 0 && (best == null || matchCount > best.Value.MatchCount))
            {
                best = new MatchResult(fileName, file.FullName, matchCount, prompts.Count);
            }
        }

        return best;
    }

    private void SetTerminalVerificationStatus(TerminalVerificationStatus status)
    {
        if (TerminalVerificationStatus == status) return;
        TerminalVerificationStatus = status;
        OnTerminalVerificationStatusChanged?.Invoke(status);
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
