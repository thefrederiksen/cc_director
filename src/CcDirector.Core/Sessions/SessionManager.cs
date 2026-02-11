using System.Collections.Concurrent;
using System.Diagnostics;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages all active sessions. Creates, tracks, and kills sessions.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _claudeSessionMap = new();
    private readonly AgentOptions _options;
    private readonly Action<string>? _log;

    public AgentOptions Options => _options;

    public SessionManager(AgentOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>Create a new ConPty session that spawns claude.exe in the given repo path.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs = null)
    {
        return CreateSession(repoPath, claudeArgs, SessionBackendType.ConPty, resumeSessionId: null);
    }

    /// <summary>Create a new session with the specified backend type.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType)
    {
        return CreateSession(repoPath, claudeArgs, backendType, resumeSessionId: null);
    }

    /// <summary>Create a session, optionally resuming a previous Claude session.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType, string? resumeSessionId)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        // Add --resume flag if resuming a previous session
        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            args = $"{args} --resume {resumeSessionId}".Trim();
            _log?.Invoke($"Resuming Claude session {resumeSessionId}");
        }

        ISessionBackend backend = backendType switch
        {
            SessionBackendType.ConPty => new ConPtyBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.Pipe => new PipeBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.Embedded => throw new InvalidOperationException(
                "Use CreateEmbeddedSession for embedded mode - requires WPF backend."),
            _ => throw new ArgumentOutOfRangeException(nameof(backendType))
        };

        var session = new Session(id, repoPath, repoPath, claudeArgs, backend, backendType);

        try
        {
            // Get initial terminal dimensions (default 120x30)
            backend.Start(_options.ClaudePath, args, repoPath, 120, 30);
            session.MarkRunning();

            _sessions[id] = session;
            var resumeInfo = !string.IsNullOrEmpty(resumeSessionId) ? $", Resume={resumeSessionId[..8]}..." : "";
            _log?.Invoke($"Session {id} created for repo {repoPath} (PID {backend.ProcessId}, Backend={backendType}{resumeInfo}).");

            return session;
        }
        catch (Exception ex)
        {
            session.MarkFailed();
            _log?.Invoke($"Failed to create session for {repoPath}: {ex.Message}");
            session.Dispose();
            throw;
        }
    }

    /// <summary>Create a new pipe mode session for the given repo path.
    /// No process is spawned until the user sends a prompt.</summary>
    public Session CreatePipeModeSession(string repoPath, string? claudeArgs = null)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        var backend = new PipeBackend(_options.DefaultBufferSizeBytes);
        backend.Start(_options.ClaudePath, args, repoPath, 120, 30);

        var session = new Session(id, repoPath, repoPath, claudeArgs, backend, SessionBackendType.Pipe);
        session.MarkRunning();

        _sessions[id] = session;
        _log?.Invoke($"Pipe mode session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>
    /// Create an embedded mode session. The WPF layer must provide the backend
    /// since EmbeddedBackend depends on WPF components.
    /// </summary>
    public Session CreateEmbeddedSession(string repoPath, string? claudeArgs, ISessionBackend embeddedBackend)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();

        var session = new Session(id, repoPath, repoPath, claudeArgs, embeddedBackend, SessionBackendType.Embedded);
        session.MarkRunning();

        _sessions[id] = session;
        _log?.Invoke($"Embedded session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>Get a session by ID.</summary>
    public Session? GetSession(Guid id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>List all sessions.</summary>
    public IReadOnlyCollection<Session> ListSessions() => _sessions.Values.ToList().AsReadOnly();

    /// <summary>Kill a session by ID.</summary>
    public async Task KillSessionAsync(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            throw new KeyNotFoundException($"Session {id} not found.");

        await session.KillAsync(_options.GracefulShutdownTimeoutSeconds * 1000);
    }

    /// <summary>Return PIDs of all tracked embedded sessions.</summary>
    public HashSet<int> GetTrackedProcessIds()
        => _sessions.Values
            .Where(s => s.BackendType == SessionBackendType.Embedded && s.ProcessId > 0)
            .Select(s => s.ProcessId)
            .ToHashSet();

    /// <summary>Scan for orphaned claude.exe processes on startup.</summary>
    public void ScanForOrphans()
    {
        try
        {
            var claudeProcesses = Process.GetProcessesByName("claude");
            if (claudeProcesses.Length > 0)
            {
                _log?.Invoke(
                    $"Found {claudeProcesses.Length} orphaned claude.exe process(es). " +
                    "Cannot re-attach ConPTY. Consider killing them manually if they are from a previous run.");

                foreach (var proc in claudeProcesses)
                {
                    _log?.Invoke($"  Orphan PID {proc.Id}, started {proc.StartTime}");
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Error scanning for orphaned claude.exe processes: {ex.Message}");
        }
    }

    /// <summary>Remove a session from tracking (dispose and clean up).</summary>
    public void RemoveSession(Guid id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            // Remove any Claude session mapping
            if (session.ClaudeSessionId != null)
                _claudeSessionMap.TryRemove(session.ClaudeSessionId, out _);

            session.Dispose();
            _log?.Invoke($"Session {id} removed.");
        }
    }

    /// <summary>Kill all sessions (used during graceful shutdown).</summary>
    public async Task KillAllSessionsAsync()
    {
        var tasks = _sessions.Values
            .Where(s => s.Status is SessionStatus.Running or SessionStatus.Starting)
            .Select(s => s.KillAsync(_options.GracefulShutdownTimeoutSeconds * 1000))
            .ToArray();

        if (tasks.Length > 0)
        {
            _log?.Invoke($"Killing {tasks.Length} active session(s)...");
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>Fires when a Claude session is registered to a Director session.</summary>
    public event Action<Session, string>? OnClaudeSessionRegistered;

    /// <summary>Register a Claude session_id -> Director session mapping.</summary>
    public void RegisterClaudeSession(string claudeSessionId, Guid directorSessionId)
    {
        _claudeSessionMap[claudeSessionId] = directorSessionId;
        if (_sessions.TryGetValue(directorSessionId, out var session))
        {
            session.ClaudeSessionId = claudeSessionId;
            // Refresh Claude metadata now that we have the session ID
            session.RefreshClaudeMetadata();
            // Notify listeners
            OnClaudeSessionRegistered?.Invoke(session, claudeSessionId);
        }
        _log?.Invoke($"Registered Claude session {claudeSessionId} -> Director session {directorSessionId}.");
    }

    /// <summary>Look up a Director session by its Claude session_id.</summary>
    public Session? GetSessionByClaudeId(string claudeSessionId)
    {
        if (_claudeSessionMap.TryGetValue(claudeSessionId, out var id))
            return GetSession(id);
        return null;
    }

    /// <summary>
    /// Find an unmatched session (no ClaudeSessionId set) by matching cwd,
    /// falling back to the oldest unmatched session.
    /// </summary>
    public Session? FindUnmatchedSession(string? cwd)
    {
        var unmatched = _sessions.Values
            .Where(s => s.ClaudeSessionId == null && s.Status == SessionStatus.Running)
            .OrderBy(s => s.CreatedAt)
            .ToList();

        if (unmatched.Count == 0) return null;

        if (!string.IsNullOrEmpty(cwd))
        {
            var byPath = unmatched.FirstOrDefault(s =>
                string.Equals(
                    Path.GetFullPath(s.WorkingDirectory).TrimEnd('\\', '/'),
                    Path.GetFullPath(cwd).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));
            if (byPath != null) return byPath;
        }

        return unmatched[0];
    }

    /// <summary>
    /// Save state of all running embedded sessions to the store without needing HWNDs.
    /// Used for incremental persistence during normal operation.
    /// ConsoleHwnd is written as 0; Reattach discovers the HWND via AttachConsole.
    /// </summary>
    public void SaveCurrentState(SessionStateStore store)
    {
        var persisted = _sessions.Values
            .Where(s => s.BackendType == SessionBackendType.Embedded && s.Status == SessionStatus.Running)
            .Select(s => new PersistedSession
            {
                Id = s.Id,
                RepoPath = s.RepoPath,
                WorkingDirectory = s.WorkingDirectory,
                ClaudeArgs = s.ClaudeArgs,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                EmbeddedProcessId = s.ProcessId,
                ConsoleHwnd = 0,
                ClaudeSessionId = s.ClaudeSessionId,
                ActivityState = s.ActivityState,
                CreatedAt = s.CreatedAt,
            })
            .ToList();

        store.Save(persisted);
        _log?.Invoke($"Saved {persisted.Count} session(s) to state store.");
    }

    /// <summary>
    /// Save state of all running embedded sessions to the store.
    /// The getHwnd delegate maps session ID -> console HWND (as long), provided by the WPF layer.
    /// </summary>
    public void SaveSessionState(SessionStateStore store, Func<Guid, long> getHwnd)
    {
        var persisted = _sessions.Values
            .Where(s => s.BackendType == SessionBackendType.Embedded && s.Status == SessionStatus.Running)
            .Select(s => new PersistedSession
            {
                Id = s.Id,
                RepoPath = s.RepoPath,
                WorkingDirectory = s.WorkingDirectory,
                ClaudeArgs = s.ClaudeArgs,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                EmbeddedProcessId = s.ProcessId,
                ConsoleHwnd = getHwnd(s.Id),
                ClaudeSessionId = s.ClaudeSessionId,
                ActivityState = s.ActivityState,
                CreatedAt = s.CreatedAt,
            })
            .ToList();

        store.Save(persisted);
        _log?.Invoke($"Saved {persisted.Count} session(s) to state store.");
    }

    /// <summary>Restore a single persisted embedded session into tracking.
    /// The WPF layer must provide the reattached backend.</summary>
    public Session RestoreEmbeddedSession(PersistedSession ps, ISessionBackend embeddedBackend)
    {
        var session = new Session(
            ps.Id, ps.RepoPath, ps.WorkingDirectory, ps.ClaudeArgs,
            embeddedBackend, ps.ClaudeSessionId, ps.ActivityState, ps.CreatedAt,
            ps.CustomName, ps.CustomColor);

        _sessions[session.Id] = session;

        if (ps.ClaudeSessionId != null)
            _claudeSessionMap[ps.ClaudeSessionId] = session.Id;

        _log?.Invoke($"Restored session {session.Id} (PID {session.ProcessId}).");
        return session;
    }

    /// <summary>
    /// Load persisted sessions from the store. Returns PersistedSession records
    /// for the WPF layer to reattach backends. Call RestoreEmbeddedSession for each
    /// successful reattach.
    /// </summary>
    public List<PersistedSession> LoadPersistedSessions(SessionStateStore store)
    {
        var persisted = store.Load();
        var valid = new List<PersistedSession>();

        foreach (var ps in persisted)
        {
            try
            {
                var proc = Process.GetProcessById(ps.EmbeddedProcessId);
                if (proc.HasExited)
                {
                    _log?.Invoke($"Persisted session {ps.Id} PID {ps.EmbeddedProcessId} has exited, skipping.");
                    proc.Dispose();
                    continue;
                }
                proc.Dispose();
                valid.Add(ps);
            }
            catch
            {
                _log?.Invoke($"Persisted session {ps.Id} PID {ps.EmbeddedProcessId} not found, skipping.");
            }
        }

        _log?.Invoke($"Found {valid.Count}/{persisted.Count} valid persisted session(s).");

        // Re-save with only the live sessions (prunes dead entries)
        store.Save(valid);

        return valid;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
        _claudeSessionMap.Clear();
    }
}
