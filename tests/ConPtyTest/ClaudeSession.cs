using System.Windows;
using ConPtyTest.ConPty;
using ConPtyTest.Controls;
using ConPtyTest.Memory;

namespace ConPtyTest;

/// <summary>
/// Encapsulates all state for a single Claude terminal session.
/// </summary>
public sealed class ClaudeSession : IDisposable
{
    private PseudoConsole? _console;
    private ProcessHost? _processHost;
    private CircularTerminalBuffer? _buffer;
    private TerminalControl? _terminal;
    private bool _disposed;

    public int SessionId { get; }
    public string Status { get; private set; } = "Not Started";
    public int ProcessId => _processHost?.ProcessId ?? 0;
    public TerminalControl? Terminal => _terminal;

    public event Action<ClaudeSession, string>? StatusChanged;
    public event Action<ClaudeSession, int>? ProcessExited;

    public ClaudeSession(int sessionId)
    {
        SessionId = sessionId;
    }

    /// <summary>
    /// Create terminal control and buffer (call before Start, on UI thread).
    /// </summary>
    public void CreateTerminal()
    {
        _buffer = new CircularTerminalBuffer();
        _terminal = new TerminalControl();
        _terminal.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Start the Claude process with the given dimensions.
    /// </summary>
    public void Start(short cols, short rows, string workingDir)
    {
        if (_terminal == null || _buffer == null)
            throw new InvalidOperationException("Call CreateTerminal first.");

        Status = "Starting...";
        StatusChanged?.Invoke(this, Status);

        // Attach terminal to buffer
        _terminal.Attach(_buffer);

        // Create ConPTY with terminal dimensions
        _console = PseudoConsole.Create(cols, rows);

        // Create process host
        _processHost = new ProcessHost(_console);
        _processHost.OnExited += OnProcessExited;

        // Start claude in the specified directory
        _processHost.Start("claude", "", workingDir);

        // Start the drain loop to read output into buffer
        _processHost.StartDrainLoop(_buffer);

        // Start monitoring for process exit
        _processHost.StartExitMonitor();

        Status = "Running";
        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>
    /// Wire up terminal events to external handlers.
    /// </summary>
    public void WireTerminalEvents(Action<byte[]> inputHandler, Action<short, short> sizeChangedHandler)
    {
        if (_terminal == null) return;
        _terminal.InputReceived += inputHandler;
        _terminal.TerminalSizeChanged += sizeChangedHandler;
    }

    /// <summary>
    /// Write data to the process input.
    /// </summary>
    public void Write(byte[] data)
    {
        _processHost?.Write(data);
    }

    /// <summary>
    /// Resize the pseudo console.
    /// </summary>
    public void Resize(short cols, short rows)
    {
        try
        {
            _console?.Resize(cols, rows);
        }
        catch
        {
            // Resize may fail if console is disposed
        }
    }

    /// <summary>
    /// Get terminal dimensions.
    /// </summary>
    public (int Cols, int Rows) GetDimensions()
    {
        return _terminal?.GetDimensions() ?? (120, 30);
    }

    private void OnProcessExited(int exitCode)
    {
        Status = $"Exited ({exitCode})";
        StatusChanged?.Invoke(this, Status);
        ProcessExited?.Invoke(this, exitCode);
    }

    /// <summary>
    /// Graceful shutdown.
    /// </summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 2000)
    {
        if (_processHost != null)
        {
            try
            {
                await _processHost.GracefulShutdownAsync(timeoutMs);
            }
            catch
            {
                // Ignore shutdown errors
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _terminal?.Detach();
        _processHost?.Dispose();
        _buffer?.Dispose();
    }
}
