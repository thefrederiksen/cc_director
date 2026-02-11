using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class RecentSessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public RecentSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RecentSessionStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "recent-sessions.json");
    }

    [Fact]
    public void Load_CreatesDirectoryIfNotExists()
    {
        var subDir = Path.Combine(_tempDir, "sub", "dir");
        var filePath = Path.Combine(subDir, "recent.json");

        var store = new RecentSessionStore(filePath);
        store.Load();

        Assert.True(Directory.Exists(subDir));
    }

    [Fact]
    public void Add_InsertsAtTop()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();

        store.Add(_tempDir, "First");
        store.Add(_tempDir + "2", "Second");

        var recent = store.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("Second", recent[0].CustomName);
        Assert.Equal("First", recent[1].CustomName);
    }

    [Fact]
    public void Add_DeduplicatesByRepoAndName()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();

        store.Add(_tempDir, "MySession");
        store.Add(_tempDir, "MySession");

        var recent = store.GetRecent();
        Assert.Single(recent);
        Assert.Equal("MySession", recent[0].CustomName);
    }

    [Fact]
    public void Add_SkipsNullCustomName()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();

        store.Add(_tempDir, null);

        Assert.Empty(store.GetRecent());
    }

    [Fact]
    public void Add_SkipsWhitespaceCustomName()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();

        store.Add(_tempDir, "   ");

        Assert.Empty(store.GetRecent());
    }

    [Fact]
    public void GetRecent_ReturnsMostRecentFirst()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();

        store.Add(_tempDir, "Alpha");
        store.Add(_tempDir + "2", "Beta");
        store.Add(_tempDir + "3", "Gamma");

        var recent = store.GetRecent();
        Assert.Equal("Gamma", recent[0].CustomName);
        Assert.Equal("Beta", recent[1].CustomName);
        Assert.Equal("Alpha", recent[2].CustomName);
    }

    [Fact]
    public void Load_HandlesCorruptJson()
    {
        File.WriteAllText(_filePath, "this is not valid json!!!");

        var store = new RecentSessionStore(_filePath);
        store.Load();

        // Should not throw, should start fresh
        Assert.Empty(store.GetRecent());
    }

    [Fact]
    public void Add_PersistsToDisk()
    {
        var store1 = new RecentSessionStore(_filePath);
        store1.Load();
        store1.Add(_tempDir, "Persisted");

        // Load in a new instance
        var store2 = new RecentSessionStore(_filePath);
        store2.Load();

        var recent = store2.GetRecent();
        Assert.Single(recent);
        Assert.Equal("Persisted", recent[0].CustomName);
    }

    [Fact]
    public void UpdateClaudeSessionId_UpdatesExistingEntry()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();
        store.Add(_tempDir, "TestSession");

        // Initially no ClaudeSessionId
        Assert.Null(store.GetRecent()[0].ClaudeSessionId);

        // Update with a Claude session ID
        store.UpdateClaudeSessionId(_tempDir, "TestSession", "abc123-session-id");

        var recent = store.GetRecent();
        Assert.Single(recent);
        Assert.Equal("abc123-session-id", recent[0].ClaudeSessionId);
    }

    [Fact]
    public void UpdateClaudeSessionId_PersistsToDisk()
    {
        var store1 = new RecentSessionStore(_filePath);
        store1.Load();
        store1.Add(_tempDir, "TestSession");
        store1.UpdateClaudeSessionId(_tempDir, "TestSession", "persisted-session-id");

        // Load in a new instance
        var store2 = new RecentSessionStore(_filePath);
        store2.Load();

        var recent = store2.GetRecent();
        Assert.Single(recent);
        Assert.Equal("persisted-session-id", recent[0].ClaudeSessionId);
    }

    [Fact]
    public void UpdateClaudeSessionId_NoOpForNonExistentEntry()
    {
        var store = new RecentSessionStore(_filePath);
        store.Load();
        store.Add(_tempDir, "ExistingSession");

        // Try to update a non-existent session - should not throw
        store.UpdateClaudeSessionId(_tempDir, "NonExistent", "some-id");

        // Original entry should be unchanged
        var recent = store.GetRecent();
        Assert.Single(recent);
        Assert.Null(recent[0].ClaudeSessionId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
