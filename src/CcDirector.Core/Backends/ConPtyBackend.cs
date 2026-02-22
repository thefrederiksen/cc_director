using System.Text;
using CcDirector.Core.ConPty;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// ConPTY-based session backend. Uses Windows Pseudo Console for terminal emulation.
/// Process output is captured to a CircularTerminalBuffer for WPF rendering.
/// </summary>
public sealed class ConPtyBackend : ISessionBackend
{
    private PseudoConsole? _console;
    private ProcessHost? _processHost;
    private CircularTerminalBuffer? _buffer;
    private bool _disposed;
    private string _status = "Not Started";
    private string _workingDir = string.Empty;

    public int ProcessId => _processHost?.ProcessId ?? 0;
    public string Status => _status;
    public bool IsRunning => _processHost != null && !HasExited;
    public bool HasExited => _processHost == null || _status.StartsWith("Exited");
    public CircularTerminalBuffer? Buffer => _buffer;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// Create a ConPtyBackend with the specified buffer size.
    /// </summary>
    /// <param name="bufferSizeBytes">Size of the circular terminal buffer in bytes.</param>
    public ConPtyBackend(int bufferSizeBytes = 2 * 1024 * 1024)
    {
        _buffer = new CircularTerminalBuffer(bufferSizeBytes);
    }

    public void Start(string executable, string args, string workingDir, short cols, short rows)
    {
        if (_processHost != null)
            throw new InvalidOperationException("Backend already started.");

        _workingDir = workingDir;
        SetStatus("Starting...");

        // Create ConPTY with terminal dimensions
        _console = PseudoConsole.Create(cols, rows);

        // Create process host
        _processHost = new ProcessHost(_console);
        _processHost.OnExited += OnProcessExited;

        // Start the process
        _processHost.Start(executable, args, workingDir);

        // Start the drain loop to read output into buffer
        _processHost.StartDrainLoop(_buffer!);

        // Start monitoring for process exit
        _processHost.StartExitMonitor();

        SetStatus("Running");
    }

    public void Write(byte[] data)
    {
        if (_disposed || _processHost == null) return;
        _processHost.Write(data);
    }

    public async Task SendTextAsync(string text)
    {
        if (_disposed || _processHost == null) return;

        string textToSend;
        if (LargeInputHandler.IsLargeInput(text) && !string.IsNullOrEmpty(_workingDir))
        {
            // Write to temp file and send @filepath
            var tempPath = LargeInputHandler.CreateTempFile(text, _workingDir);
            textToSend = $"@{tempPath}";
            FileLog.Write($"[ConPtyBackend] Large input ({text.Length} chars), using temp file reference: {textToSend}");
        }
        else
        {
            textToSend = text;
        }

        var textBytes = Encoding.UTF8.GetBytes(textToSend);
        _processHost.Write(textBytes);

        // Brief delay so TUI processes text before Enter
        await Task.Delay(50);

        // Send Enter (carriage return)
        _processHost.Write(new byte[] { 0x0D });
    }

    public Task SendEnterAsync()
    {
        if (_disposed || _processHost == null) return Task.CompletedTask;
        _processHost.Write(new byte[] { 0x0D });
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _console == null) return;
        try
        {
            _console.Resize(cols, rows);
        }
        catch
        {
            // Resize may fail if console is disposed
        }
    }

    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed || _processHost == null) return;

        SetStatus("Exiting...");
        await _processHost.GracefulShutdownAsync(timeoutMs);
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

        _processHost?.Dispose();
        _buffer?.Dispose();

        _processHost = null;
        _console = null;
        _buffer = null;
    }
}
