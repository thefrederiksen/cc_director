using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf.Backends;

/// <summary>
/// Embedded mode backend. Uses a real console window overlaid on the WPF application.
/// The console window is managed by EmbeddedConsoleHost which handles all the
/// workarounds for Z-order, border stripping, and text input.
/// </summary>
public sealed class EmbeddedBackend : ISessionBackend
{
    private EmbeddedConsoleHost? _host;
    private bool _disposed;
    private string _status = "Not Started";

    public int ProcessId => _host?.ProcessId ?? 0;
    public string Status => _status;
    public bool IsRunning => _host != null && !_host.HasExited;
    public bool HasExited => _host?.HasExited ?? true;
    public bool IsVisible => _host?.IsVisible ?? false;

    /// <summary>
    /// Embedded mode doesn't use a buffer - the real console window handles display.
    /// Returns null.
    /// </summary>
    public CircularTerminalBuffer? Buffer => null;

    /// <summary>
    /// The console window handle, for positioning and Z-order management.
    /// </summary>
    public IntPtr ConsoleHwnd => _host?.ConsoleHwnd ?? IntPtr.Zero;

    /// <summary>
    /// The underlying EmbeddedConsoleHost for direct access when needed.
    /// </summary>
    public EmbeddedConsoleHost? Host => _host;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// Create a new EmbeddedBackend.
    /// </summary>
    public EmbeddedBackend()
    {
    }

    /// <summary>
    /// Create an EmbeddedBackend by reattaching to an existing process.
    /// </summary>
    public static EmbeddedBackend? Reattach(int processId, IntPtr persistedHwnd)
    {
        var host = EmbeddedConsoleHost.Reattach(processId, persistedHwnd);
        if (host == null) return null;

        var backend = new EmbeddedBackend();
        backend._host = host;
        backend._status = "Running";

        host.OnProcessExited += exitCode =>
        {
            backend.SetStatus($"Exited ({exitCode})");
            backend.ProcessExited?.Invoke(exitCode);
        };

        return backend;
    }

    /// <summary>
    /// Start the embedded console process.
    /// </summary>
    public void Start(string executable, string args, string workingDir, short cols, short rows)
    {
        if (_host != null)
            throw new InvalidOperationException("Backend already started.");

        SetStatus("Starting...");

        _host = new EmbeddedConsoleHost();
        _host.OnProcessExited += OnProcessExited;
        _host.StartProcess(executable, args, workingDir);

        SetStatus("Running");
    }

    /// <summary>
    /// Write raw bytes. For embedded mode, this is not directly supported.
    /// Use SendTextAsync instead.
    /// </summary>
    public void Write(byte[] data)
    {
        // Embedded mode doesn't support direct byte writes
        // The console handles its own input
        System.Diagnostics.Debug.WriteLine("[EmbeddedBackend] Write() called - use SendTextAsync instead");
    }

    /// <summary>
    /// Send text to the embedded console using the two-tier approach
    /// (WriteConsoleInput with clipboard fallback).
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || _host == null) return;
        await _host.SendTextAsync(text);
    }

    /// <summary>
    /// Send just an Enter keystroke (for retry scenarios).
    /// </summary>
    public async Task SendEnterAsync()
    {
        if (_disposed || _host == null) return;
        await _host.SendEnterAsync();
    }

    /// <summary>
    /// Resize is handled automatically by the console window.
    /// </summary>
    public void Resize(short cols, short rows)
    {
        // No-op - console window auto-sizes
    }

    /// <summary>
    /// Kill the console process.
    /// </summary>
    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed || _host == null) return Task.CompletedTask;

        SetStatus("Exiting...");
        _host.KillProcess();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Show the console window and force it above the WPF owner.
    /// </summary>
    public void Show()
    {
        _host?.Show();
    }

    /// <summary>
    /// Hide the console window.
    /// </summary>
    public void Hide()
    {
        _host?.Hide();
    }

    /// <summary>
    /// Update the console window position to cover the given screen rectangle.
    /// </summary>
    public void UpdatePosition(System.Windows.Rect screenRect)
    {
        _host?.UpdatePosition(screenRect);
    }

    /// <summary>
    /// Set the WPF window as owner of the console window for Z-order management.
    /// </summary>
    public void SetOwner(IntPtr ownerHwnd)
    {
        _host?.SetOwner(ownerHwnd);
    }

    /// <summary>
    /// Ensure the console window stays above the WPF window (TOPMOST flash).
    /// Call this periodically.
    /// </summary>
    public void EnsureZOrder()
    {
        _host?.EnsureZOrder();
    }

    /// <summary>
    /// Detach from the console process without killing it.
    /// The process continues running independently.
    /// </summary>
    public void Detach()
    {
        if (_disposed || _host == null) return;
        _host.Detach();
        _host = null;
        _disposed = true;
    }

    private void OnProcessExited(int exitCode)
    {
        SetStatus($"Exited ({exitCode})");
        ProcessExited?.Invoke(exitCode);
    }

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _host?.Dispose();
        _host = null;
    }
}
