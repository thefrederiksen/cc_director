using CcDirector.Core.Memory;

namespace CcDirector.Core.Backends;

/// <summary>
/// Abstraction for different session backend implementations.
/// Each backend handles process lifecycle, I/O, and terminal management differently.
/// </summary>
public interface ISessionBackend : IDisposable
{
    /// <summary>The process ID of the running Claude process, or 0 if not running.</summary>
    int ProcessId { get; }

    /// <summary>Current status description (e.g., "Starting", "Running", "Exited (0)").</summary>
    string Status { get; }

    /// <summary>True if the backend has a running process.</summary>
    bool IsRunning { get; }

    /// <summary>True if the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>The terminal buffer for output. May be null for backends that don't buffer (Embedded).</summary>
    CircularTerminalBuffer? Buffer { get; }

    /// <summary>Fires when the status changes.</summary>
    event Action<string>? StatusChanged;

    /// <summary>Fires when the process exits. Argument is exit code.</summary>
    event Action<int>? ProcessExited;

    /// <summary>
    /// Start the Claude process.
    /// </summary>
    /// <param name="executable">Path to claude executable.</param>
    /// <param name="args">Command line arguments.</param>
    /// <param name="workingDir">Working directory for the process.</param>
    /// <param name="cols">Terminal columns (ignored by some backends).</param>
    /// <param name="rows">Terminal rows (ignored by some backends).</param>
    void Start(string executable, string args, string workingDir, short cols, short rows);

    /// <summary>
    /// Write raw bytes to the process input.
    /// For ConPty: writes to PTY input pipe.
    /// For Embedded: uses WriteConsoleInput or clipboard.
    /// For Pipe: not supported (use SendPromptAsync instead).
    /// </summary>
    void Write(byte[] data);

    /// <summary>
    /// Send text followed by Enter to the process.
    /// </summary>
    Task SendTextAsync(string text);

    /// <summary>
    /// Send just an Enter keystroke to the process.
    /// Used by the Enter retry mechanism when the initial Enter doesn't register.
    /// </summary>
    Task SendEnterAsync() => Task.CompletedTask;

    /// <summary>
    /// Resize the terminal. Only meaningful for ConPty backend.
    /// </summary>
    void Resize(short cols, short rows);

    /// <summary>
    /// Gracefully shutdown the process (send Ctrl+C, wait, then force kill if needed).
    /// </summary>
    Task GracefulShutdownAsync(int timeoutMs = 5000);
}

/// <summary>
/// Enum identifying the backend type.
/// </summary>
public enum SessionBackendType
{
    /// <summary>ConPTY-based terminal with WPF rendering.</summary>
    ConPty,

    /// <summary>Real console window embedded/overlaid on WPF.</summary>
    Embedded,

    /// <summary>Pipe mode - stateless, spawns process per prompt.</summary>
    Pipe
}
