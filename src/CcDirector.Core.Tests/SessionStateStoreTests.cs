using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class SessionStateStoreTests
{
    [Fact]
    public void SaveAndLoad_SingleSession_RoundTrips()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-claude-session-123",
                CustomName = "Test Session",
                CustomColor = "#FF0000",
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Save
            store.Save(new[] { session });

            // Verify file exists and has content
            Assert.True(File.Exists(tempFile), "File should exist after save");
            var json = File.ReadAllText(tempFile);
            Assert.Contains("test-claude-session-123", json);

            // Load
            var loaded = store.Load();

            // Verify
            Assert.Single(loaded);
            Assert.Equal(session.Id, loaded[0].Id);
            Assert.Equal("test-claude-session-123", loaded[0].ClaudeSessionId);
            Assert.Equal("Test Session", loaded[0].CustomName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyList()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, "[]");
            var store = new SessionStateStore(tempFile);

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
    public void Load_NonExistentFile_ReturnsEmptyList()
    {
        var store = new SessionStateStore(@"C:\nonexistent\path\sessions.json");

        var loaded = store.Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void PersistedSession_PendingPromptText_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-abc",
                PendingPromptText = "fix the login bug in auth.cs",
                ActivityState = ActivityState.WaitingForInput,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Act
            store.Save(new[] { session });
            var loaded = store.Load();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("fix the login bug in auth.cs", loaded[0].PendingPromptText);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PersistedSession_NullPendingPromptText_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-xyz",
                PendingPromptText = null,
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Act
            store.Save(new[] { session });
            var loaded = store.Load();

            // Assert
            Assert.Single(loaded);
            Assert.Null(loaded[0].PendingPromptText);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Session_RestoreConstructor_SetsPendingPromptText()
    {
        // Arrange
        var backend = new StubBackend();
        var id = Guid.NewGuid();

        // Act
        var session = new Session(
            id,
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-123",
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow,
            customName: "My Session",
            customColor: "#0000FF",
            pendingPromptText: "implement the feature");

        // Assert
        Assert.Equal("implement the feature", session.PendingPromptText);
        Assert.Equal("My Session", session.CustomName);
        Assert.Equal("#0000FF", session.CustomColor);
        Assert.Equal("claude-123", session.ClaudeSessionId);

        session.Dispose();
    }

    [Fact]
    public void Session_RestoreConstructor_NullPendingPromptText_DefaultsToNull()
    {
        // Arrange
        var backend = new StubBackend();

        // Act — omit pendingPromptText (defaults to null)
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-456",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);

        // Assert
        Assert.Null(session.PendingPromptText);

        session.Dispose();
    }

    /// <summary>Minimal backend stub for Session constructor tests.</summary>
    private sealed class StubBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Stub";
        public bool IsRunning => false;
        public bool HasExited => true;
        public CircularTerminalBuffer? Buffer => null;

        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;

        public void Start(string executable, string args, string workingDir, short cols, short rows) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;

        public void Dispose()
        {
            // Suppress unused warnings
            _ = StatusChanged;
            _ = ProcessExited;
        }
    }
}
