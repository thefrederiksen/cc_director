namespace CcDirector.Core.Utilities;

/// <summary>
/// Monitors local drives for files named "NUL" and deletes them.
/// Windows reserves the NUL device name, but actual files can be created via \\?\ prefix paths.
/// These files are hard to delete normally and clutter the filesystem.
/// </summary>
public sealed class NulFileWatcher : IDisposable
{
    private readonly List<string> _drivePaths;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Task? _scanTask;
    private bool _disposed;

    /// <summary>Raised when a NUL file is successfully deleted.</summary>
    public Action<string>? OnNulFileDeleted;

    /// <summary>Raised when deletion of a NUL file fails.</summary>
    public Action<string, Exception>? OnDeletionFailed;

    /// <summary>
    /// Creates a NulFileWatcher that monitors the specified drives, or all local fixed drives if none specified.
    /// </summary>
    /// <param name="drivePaths">Specific drive paths to monitor (e.g., "C:\", "D:\"). If null or empty, monitors all local fixed drives.</param>
    /// <param name="log">Optional logging callback.</param>
    public NulFileWatcher(IEnumerable<string>? drivePaths = null, Action<string>? log = null)
    {
        _drivePaths = drivePaths?.ToList() ?? GetAllLocalDrives();
        _log = log;
    }

    /// <summary>
    /// Creates a NulFileWatcher that monitors a single drive.
    /// </summary>
    /// <param name="drivePath">Single drive path to monitor. If null, monitors all local fixed drives.</param>
    /// <param name="log">Optional logging callback.</param>
    public NulFileWatcher(string? drivePath, Action<string>? log)
        : this(drivePath != null ? new[] { drivePath } : null, log)
    {
    }

    /// <summary>
    /// Gets all local fixed drives (e.g., C:\, D:\).
    /// </summary>
    private static List<string> GetAllLocalDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }

    public void Start()
    {
        _log?.Invoke($"Starting NUL file watcher for drives: {string.Join(", ", _drivePaths)}");

        foreach (var drivePath in _drivePaths)
        {
            try
            {
                var watcher = new FileSystemWatcher(drivePath)
                {
                    Filter = "NUL",
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                watcher.Created += OnFileCreated;
                _watchers.Add(watcher);
                _log?.Invoke($"Watching drive: {drivePath}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Failed to create watcher for {drivePath}: {ex.Message}");
            }
        }

        _scanTask = ScanAllDrivesAsync(_cts.Token);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _log?.Invoke($"NUL file detected by watcher: {e.FullPath}");
        TryDeleteAndRaiseEvents(e.FullPath);
    }

    internal Task ScanAllDrivesAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            foreach (var drivePath in _drivePaths)
            {
                if (ct.IsCancellationRequested) return;
                _log?.Invoke($"Scanning drive for NUL files: {drivePath}");
                ScanDirectory(drivePath, ct);
            }
            _log?.Invoke("Initial NUL file scan complete.");
        }, ct);
    }

    // Keep this for backward compatibility with tests
    internal Task ScanDriveAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            foreach (var drivePath in _drivePaths)
            {
                if (ct.IsCancellationRequested) return;
                ScanDirectory(drivePath, ct);
            }
        }, ct);
    }

    private void ScanDirectory(string directory, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var dirName = Path.GetFileName(directory);
        if (dirName is "$Recycle.Bin" or "System Volume Information")
            return;

        try
        {
            // Check for NUL file in this directory
            var nulPath = Path.Combine(directory, "NUL");
            var extendedPath = ToExtendedLengthPath(nulPath);

            if (File.Exists(extendedPath))
            {
                _log?.Invoke($"NUL file found by scan: {nulPath}");
                TryDeleteAndRaiseEvents(nulPath);
            }

            // Recursively scan subdirectories
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                if (ct.IsCancellationRequested) return;
                ScanDirectory(subDir, ct);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (IOException)
        {
            // Skip directories with I/O errors
        }
    }

    private void TryDeleteAndRaiseEvents(string path)
    {
        try
        {
            if (TryDeleteNulFile(path))
            {
                _log?.Invoke($"Deleted NUL file: {path}");
                OnNulFileDeleted?.Invoke(path);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Failed to delete NUL file {path}: {ex.Message}");
            OnDeletionFailed?.Invoke(path, ex);
        }
    }

    /// <summary>
    /// Attempts to delete a NUL file using the \\?\ extended-length path prefix.
    /// Returns true if the file existed and was deleted.
    /// </summary>
    internal static bool TryDeleteNulFile(string path)
    {
        var extendedPath = ToExtendedLengthPath(path);
        if (!File.Exists(extendedPath))
            return false;

        File.Delete(extendedPath);
        return true;
    }

    /// <summary>
    /// Adds the \\?\ extended-length path prefix if not already present.
    /// This is required to interact with files named NUL, CON, PRN, etc.
    /// on Windows - without it, the OS interprets these as device names.
    /// </summary>
    internal static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\"))
            return path;

        return @"\\?\" + path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        _cts.Dispose();
    }
}
