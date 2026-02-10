using System.Collections.Concurrent;
using System.Diagnostics;
using CcDirector.Core.Configuration;
using CcDirector.Core.ConPty;
using CcDirector.Core.Memory;

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

    /// <summary>Create a new session that spawns claude.exe in the given repo path.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs = null)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        var buffer = new CircularTerminalBuffer(_options.DefaultBufferSizeBytes);
        var console = PseudoConsole.Create(120, 30);
        var processHost = new ProcessHost(console);

        var session = new Session(id, repoPath, repoPath, claudeArgs, console, processHost, buffer);

        try
        {
            string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;
            processHost.Start(_options.ClaudePath, args, repoPath);
            session.Status = SessionStatus.Running;

            processHost.OnExited += exitCode =>
            {
                session.ExitCode = exitCode;
                session.Status = SessionStatus.Exited;
                session.HandlePipeEvent(new Pipes.PipeMessage { HookEventName = "SessionEnd" });
                _log?.Invoke($"Session {id} exited with code {exitCode}.");
            };

            processHost.StartDrainLoop(buffer);
            processHost.StartExitMonitor();

            _sessions[id] = session;
            _log?.Invoke($"Session {id} created for repo {repoPath} (PID {processHost.ProcessId}).");

            return session;
        }
        catch (Exception ex)
        {
            session.Status = SessionStatus.Failed;
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
        var buffer = new CircularTerminalBuffer(_options.DefaultBufferSizeBytes);
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        var session = new Session(id, repoPath, repoPath, claudeArgs, _options.ClaudePath, args, buffer);

        _sessions[id] = session;
        _log?.Invoke($"Pipe mode session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>Create an embedded mode session. The WPF layer spawns the process
    /// inside an EmbeddedConsoleHost; no buffer or ConPTY is needed.</summary>
    public Session CreateEmbeddedSession(string repoPath, string? claudeArgs = null)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        var session = new Session(id, repoPath, repoPath, args);

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
            .Where(s => s.Mode == SessionMode.Embedded && s.EmbeddedProcessId > 0)
            .Select(s => s.EmbeddedProcessId)
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

    /// <summary>Register a Claude session_id → Director session mapping.</summary>
    public void RegisterClaudeSession(string claudeSessionId, Guid directorSessionId)
    {
        _claudeSessionMap[claudeSessionId] = directorSessionId;
        if (_sessions.TryGetValue(directorSessionId, out var session))
            session.ClaudeSessionId = claudeSessionId;
        _log?.Invoke($"Registered Claude session {claudeSessionId} → Director session {directorSessionId}.");
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
            .Where(s => s.Mode == SessionMode.Embedded && s.Status == SessionStatus.Running)
            .Select(s => new PersistedSession
            {
                Id = s.Id,
                RepoPath = s.RepoPath,
                WorkingDirectory = s.WorkingDirectory,
                ClaudeArgs = s.ClaudeArgs,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                EmbeddedProcessId = s.EmbeddedProcessId,
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
    /// The getHwnd delegate maps session ID → console HWND (as long), provided by the WPF layer.
    /// </summary>
    public void SaveSessionState(SessionStateStore store, Func<Guid, long> getHwnd)
    {
        var persisted = _sessions.Values
            .Where(s => s.Mode == SessionMode.Embedded && s.Status == SessionStatus.Running)
            .Select(s => new PersistedSession
            {
                Id = s.Id,
                RepoPath = s.RepoPath,
                WorkingDirectory = s.WorkingDirectory,
                ClaudeArgs = s.ClaudeArgs,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                EmbeddedProcessId = s.EmbeddedProcessId,
                ConsoleHwnd = getHwnd(s.Id),
                ClaudeSessionId = s.ClaudeSessionId,
                ActivityState = s.ActivityState,
                CreatedAt = s.CreatedAt,
            })
            .ToList();

        store.Save(persisted);
        _log?.Invoke($"Saved {persisted.Count} session(s) to state store.");
    }

    /// <summary>Restore a single persisted embedded session into tracking.</summary>
    public Session RestoreEmbeddedSession(PersistedSession ps)
    {
        var session = new Session(
            ps.Id, ps.RepoPath, ps.WorkingDirectory, ps.ClaudeArgs,
            ps.EmbeddedProcessId, ps.ClaudeSessionId, ps.ActivityState, ps.CreatedAt,
            ps.CustomName, ps.CustomColor);

        _sessions[session.Id] = session;

        if (ps.ClaudeSessionId != null)
            _claudeSessionMap[ps.ClaudeSessionId] = session.Id;

        _log?.Invoke($"Restored session {session.Id} (PID {ps.EmbeddedProcessId}).");
        return session;
    }

    /// <summary>
    /// Load persisted sessions from the store. Validates each PID is still alive,
    /// restores valid ones, and returns tuples of (Session, PersistedSession) so
    /// the WPF layer can use the stored HWND for reattach.
    /// </summary>
    public List<(Session Session, PersistedSession Persisted)> LoadPersistedSessions(SessionStateStore store)
    {
        var persisted = store.Load();
        var restored = new List<(Session, PersistedSession)>();

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
            }
            catch
            {
                _log?.Invoke($"Persisted session {ps.Id} PID {ps.EmbeddedProcessId} not found, skipping.");
                continue;
            }

            restored.Add((RestoreEmbeddedSession(ps), ps));
        }

        _log?.Invoke($"Loaded {restored.Count}/{persisted.Count} persisted session(s).");

        // Re-save with only the live sessions (prunes dead entries)
        store.Save(restored.Select(r => r.Item2).ToList());

        return restored;
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
