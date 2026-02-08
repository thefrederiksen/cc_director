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
