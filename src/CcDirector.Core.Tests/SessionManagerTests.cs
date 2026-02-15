using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class SessionManagerTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe", // Use cmd.exe for testing - it's always available
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void CreateSession_InvalidPath_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreateSession(@"C:\nonexistent\path\that\does\not\exist"));
    }

    [Fact]
    public void GetSession_UnknownId_ReturnsNull()
    {
        var result = _manager.GetSession(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void ListSessions_Empty_ReturnsEmpty()
    {
        var sessions = _manager.ListSessions();
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task KillSession_UnknownId_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _manager.KillSessionAsync(Guid.NewGuid()));
    }

    [Fact]
    public void CreateSession_WithCmdExe_Succeeds()
    {
        var tempDir = Path.GetTempPath();
        var session = _manager.CreateSession(tempDir);

        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.Equal(tempDir, session.RepoPath);
        Assert.True(session.ProcessId > 0);

        var listed = _manager.ListSessions();
        Assert.Single(listed);

        var fetched = _manager.GetSession(session.Id);
        Assert.NotNull(fetched);
        Assert.Equal(session.Id, fetched.Id);
    }

    [Fact]
    public async Task CreateAndKillSession_StatusChanges()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Equal(SessionStatus.Running, session.Status);

        await _manager.KillSessionAsync(session.Id);

        // After kill, status should be Exiting or Exited
        Assert.True(session.Status is SessionStatus.Exiting or SessionStatus.Exited);
    }

    [Fact]
    public async Task CreateSession_CmdExe_ReceivesOutput()
    {
        // Use a separate manager to avoid contention with other tests
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        using var manager = new SessionManager(options);
        var session = manager.CreateSession(Path.GetTempPath());

        // Send a command to force output
        session.SendText("echo hello");

        // Poll for output with timeout
        Assert.NotNull(session.Buffer);
        byte[] dump = Array.Empty<byte>();
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            dump = session.Buffer.DumpAll();
            if (dump.Length > 0) break;
        }

        Assert.True(dump.Length > 0, "Expected cmd.exe to produce some output.");
    }

    [Fact]
    public async Task DrainLoop_CmdExe_ReceivesBanner()
    {
        // Simplest test: does the drain loop put ANY bytes into the buffer?
        // cmd.exe should produce its "Microsoft Windows" banner without us sending anything.
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        using var manager = new SessionManager(options);
        var session = manager.CreateSession(Path.GetTempPath());

        Assert.NotNull(session.Buffer);
        long totalBytes = 0;
        for (int i = 0; i < 40; i++) // 8 seconds max
        {
            await Task.Delay(200);
            totalBytes = session.Buffer.TotalBytesWritten;
            if (totalBytes > 0) break;
        }

        var dump = session.Buffer.DumpAll();
        var output = System.Text.Encoding.UTF8.GetString(dump);

        Assert.True(totalBytes > 0,
            $"Expected cmd.exe to produce banner output but TotalBytesWritten={totalBytes}. " +
            $"Session status={session.Status}, PID={session.ProcessId}");
    }

    [Fact]
    public async Task SendText_CmdExe_CommandIsExecuted()
    {
        // Proves whether SendText actually submits a command (not just echoes text).
        // cmd.exe uses cooked mode, so if this works but Claude Code doesn't,
        // the issue is specific to raw-mode TUI apps.
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        using var manager = new SessionManager(options);
        var session = manager.CreateSession(Path.GetTempPath());

        // Wait for cmd.exe to start and show its prompt
        await Task.Delay(500);

        // Send a command with a unique marker
        session.SendText("echo SENDTEXT_MARKER_12345");

        // Poll for the marker in output
        Assert.NotNull(session.Buffer);
        string output = "";
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(200);
            var dump = session.Buffer.DumpAll();
            output = System.Text.Encoding.UTF8.GetString(dump);
            // Look for the marker in a context that proves execution (not just echo of the command line)
            // cmd.exe will show: echo SENDTEXT_MARKER_12345\r\nSENDTEXT_MARKER_12345\r\n
            // The marker appearing AFTER a newline following the command means it was executed
            if (output.Contains("SENDTEXT_MARKER_12345"))
            {
                // Count occurrences - should appear at least twice:
                // once in the command echo, once in the output
                int count = 0;
                int idx = 0;
                while ((idx = output.IndexOf("SENDTEXT_MARKER_12345", idx, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    idx += "SENDTEXT_MARKER_12345".Length;
                }
                if (count >= 2) break; // command was executed
            }
        }

        // The marker should appear at least twice: once as the typed command, once as output
        int finalCount = 0;
        int fi = 0;
        while ((fi = output.IndexOf("SENDTEXT_MARKER_12345", fi, StringComparison.Ordinal)) >= 0)
        {
            finalCount++;
            fi += "SENDTEXT_MARKER_12345".Length;
        }

        Assert.True(finalCount >= 2,
            $"Expected SENDTEXT_MARKER_12345 at least twice (command + output) but found {finalCount} time(s).\nFull output:\n{output}");
    }

    [Fact]
    public void ScanForOrphans_DoesNotThrow()
    {
        // Just verify it doesn't crash
        _manager.ScanForOrphans();
    }

    [Fact]
    public void SaveCurrentState_ConPtySession_IsPersisted()
    {
        // Create a temp file for the store
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_sessions_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            // Create a ConPty session
            var session = _manager.CreateSession(Path.GetTempPath());
            Assert.Equal(SessionBackendType.ConPty, session.BackendType);
            Assert.Equal(SessionStatus.Running, session.Status);

            // Set custom name and Claude session ID
            session.CustomName = "Test Session";
            session.CustomColor = "#FF5500";
            session.ClaudeSessionId = "test-claude-session-id";

            // Save state
            _manager.SaveCurrentState(store);

            // Load and verify
            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal(session.Id, loaded[0].Id);
            Assert.Equal(session.RepoPath, loaded[0].RepoPath);
            Assert.Equal("Test Session", loaded[0].CustomName);
            Assert.Equal("#FF5500", loaded[0].CustomColor);
            Assert.Equal("test-claude-session-id", loaded[0].ClaudeSessionId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SaveCurrentState_ExitedSessionWithoutClaudeSessionId_NotPersisted()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_sessions_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            // Create and kill a session (no ClaudeSessionId)
            var session = _manager.CreateSession(Path.GetTempPath());
            await _manager.KillSessionAsync(session.Id);

            // Wait for status to change
            for (int i = 0; i < 10 && session.Status == SessionStatus.Running; i++)
            {
                await Task.Delay(100);
            }

            // Save state - killed session without ClaudeSessionId should not be persisted
            _manager.SaveCurrentState(store);

            var loaded = store.Load();
            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SaveCurrentState_ExitedSessionWithClaudeSessionId_IsPersisted()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_sessions_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            // Create and kill a session, but set ClaudeSessionId first
            var session = _manager.CreateSession(Path.GetTempPath());
            session.ClaudeSessionId = "test-claude-session-id";

            await _manager.KillSessionAsync(session.Id);

            // Wait for status to change to Exited
            for (int i = 0; i < 10 && session.Status == SessionStatus.Running; i++)
            {
                await Task.Delay(100);
            }

            // Save state - exited session WITH ClaudeSessionId should be persisted
            _manager.SaveCurrentState(store);

            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal("test-claude-session-id", loaded[0].ClaudeSessionId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SaveCurrentState_AnyStatusWithClaudeSessionId_IsPersisted()
    {
        // This test verifies that ANY session with a ClaudeSessionId is persisted,
        // regardless of its status (Running, Exiting, Exited, Failed, etc.)
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_sessions_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            // Create session and set ClaudeSessionId
            var session = _manager.CreateSession(Path.GetTempPath());
            session.ClaudeSessionId = "test-claude-session-id";

            // Start killing - this puts session in Exiting status
            var killTask = _manager.KillSessionAsync(session.Id);

            // Immediately save while potentially in Exiting status
            _manager.SaveCurrentState(store);

            var loaded = store.Load();

            // Session should be persisted regardless of whether it's Running, Exiting, or Exited
            Assert.Single(loaded);
            Assert.Equal("test-claude-session-id", loaded[0].ClaudeSessionId);

            // Wait for kill to complete
            await killTask;
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void RemoveSession_WithClaudeSessionId_NotPersistedAfterRemoval()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_sessions_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            // Create a session and assign a ClaudeSessionId (normally persisted)
            var session = _manager.CreateSession(Path.GetTempPath());
            session.ClaudeSessionId = "test-claude-session-id";

            // Verify it would be persisted before removal
            _manager.SaveCurrentState(store);
            var loaded = store.Load();
            Assert.Single(loaded);

            // Remove the session from the manager
            _manager.RemoveSession(session.Id);

            // After removal, SaveCurrentState should NOT include the removed session
            _manager.SaveCurrentState(store);
            loaded = store.Load();
            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
