using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class NulFileWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public NulFileWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NulWatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ToExtendedLengthPath_AddsPrefix()
    {
        var result = NulFileWatcher.ToExtendedLengthPath(@"D:\path\NUL");
        Assert.Equal(@"\\?\D:\path\NUL", result);
    }

    [Fact]
    public void ToExtendedLengthPath_DoesNotDoublePrefix()
    {
        var result = NulFileWatcher.ToExtendedLengthPath(@"\\?\D:\path\NUL");
        Assert.Equal(@"\\?\D:\path\NUL", result);
    }

    [Fact]
    public void TryDeleteNulFile_DeletesNulFile()
    {
        var nulPath = Path.Combine(_tempDir, "NUL");
        var extendedPath = @"\\?\" + nulPath;

        // Create a real NUL file using the extended-length prefix
        File.WriteAllText(extendedPath, "test");
        Assert.True(File.Exists(extendedPath), "NUL file should exist after creation");

        var result = NulFileWatcher.TryDeleteNulFile(nulPath);

        Assert.True(result, "TryDeleteNulFile should return true");
        Assert.False(File.Exists(extendedPath), "NUL file should be deleted");
    }

    [Fact]
    public void TryDeleteNulFile_ReturnsFalseWhenNoFile()
    {
        var nulPath = Path.Combine(_tempDir, "NUL");
        var result = NulFileWatcher.TryDeleteNulFile(nulPath);
        Assert.False(result);
    }

    [Fact]
    public async Task Start_InitialScan_DeletesExistingNulFile()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);

        var nulPath = Path.Combine(subDir, "NUL");
        var extendedPath = @"\\?\" + nulPath;
        File.WriteAllText(extendedPath, "test");
        Assert.True(File.Exists(extendedPath), "NUL file should exist after creation");

        var tcs = new TaskCompletionSource<string>();

        using var watcher = new NulFileWatcher(_tempDir, msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
        watcher.OnNulFileDeleted = path => tcs.TrySetResult(path);
        watcher.Start();

        // Note: If cc_director is running, its NulFileWatcher may delete this file
        // before this test's watcher can, causing a timeout.
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        Assert.True(completed == tcs.Task, "Timed out waiting for NUL file deletion (is cc_director running?)");

        var deletedPath = await tcs.Task;
        Assert.Contains("NUL", deletedPath);
        Assert.False(File.Exists(extendedPath), "NUL file should have been deleted by scan");
    }

    [Fact]
    public async Task Start_Watcher_DetectsNewNulFile()
    {
        var tcs = new TaskCompletionSource<string>();

        using var watcher = new NulFileWatcher(_tempDir, msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
        watcher.OnNulFileDeleted = path => tcs.TrySetResult(path);
        watcher.Start();

        // Give the watcher a moment to initialize
        await Task.Delay(200);

        // Create a NUL file after the watcher has started
        var nulPath = Path.Combine(_tempDir, "NUL");
        var extendedPath = @"\\?\" + nulPath;
        File.WriteAllText(extendedPath, "test");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        Assert.True(completed == tcs.Task, "Timed out waiting for watcher to detect NUL file");

        var deletedPath = await tcs.Task;
        Assert.Contains("NUL", deletedPath);
    }

    [Fact]
    public async Task InitialScan_SkipsInaccessibleDirectories()
    {
        // Scan should complete without throwing even if subdirectories are inaccessible
        using var watcher = new NulFileWatcher(_tempDir, msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));

        // ScanDriveAsync is internal, call it directly
        await watcher.ScanDriveAsync(CancellationToken.None);

        // If we get here without exception, the test passes
    }

    [Fact]
    public void Dispose_StopsWatcher()
    {
        var watcher = new NulFileWatcher(_tempDir, msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
        watcher.Start();
        watcher.Dispose();

        // Should not throw — double-dispose should be safe too
        watcher.Dispose();
    }

    /// <summary>
    /// Integration test: attempts to delete a real nul file at a known location.
    /// This test will be skipped if no nul file exists at the target path.
    /// </summary>
    [Fact]
    public void TryDeleteNulFile_RealFile_Integration()
    {
        // Known locations where nul files commonly appear
        var knownPaths = new[]
        {
            @"D:\ReposFred\cc_director\nul",
            @"D:\ReposFred\cc_computer\nul",
            @"C:\ReposFred\cc_director\nul",
        };

        string? foundPath = null;
        foreach (var path in knownPaths)
        {
            var extendedPath = NulFileWatcher.ToExtendedLengthPath(path);
            if (File.Exists(extendedPath))
            {
                foundPath = path;
                break;
            }
        }

        if (foundPath == null)
        {
            // No nul file found - skip this test
            System.Diagnostics.Debug.WriteLine("[Test] No nul file found at known locations - skipping");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[Test] Found nul file at: {foundPath}");

        // Attempt deletion
        var result = NulFileWatcher.TryDeleteNulFile(foundPath);

        Assert.True(result, $"TryDeleteNulFile should return true for {foundPath}");

        var extendedAfter = NulFileWatcher.ToExtendedLengthPath(foundPath);
        Assert.False(File.Exists(extendedAfter), $"NUL file should be deleted at {foundPath}");

        System.Diagnostics.Debug.WriteLine($"[Test] Successfully deleted nul file: {foundPath}");
    }

    public void Dispose()
    {
        try
        {
            // Clean up temp directory, using extended paths for any NUL files
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    var extended = file.StartsWith(@"\\?\") ? file : @"\\?\" + file;
                    try { File.Delete(extended); } catch { }
                }
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
