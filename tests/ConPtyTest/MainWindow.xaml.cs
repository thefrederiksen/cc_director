using System.ComponentModel;
using System.Windows;
using ConPtyTest.ConPty;
using ConPtyTest.Controls;
using ConPtyTest.Memory;

namespace ConPtyTest;

public partial class MainWindow : Window
{
    private PseudoConsole? _console;
    private ProcessHost? _processHost;
    private CircularTerminalBuffer? _buffer;
    private TerminalControl? _terminal;

    // Hardcoded working directory
    private const string WorkingDir = @"D:\ReposFred\cc_director";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _buffer = new CircularTerminalBuffer();
        _terminal = new TerminalControl();

        // Wire up terminal events
        _terminal.InputReceived += OnTerminalInput;
        _terminal.TerminalSizeChanged += OnTerminalSizeChanged;

        TerminalContainer.Child = _terminal;

        // Attach terminal to buffer
        _terminal.Attach(_buffer);

        // Get initial dimensions
        var (cols, rows) = _terminal.GetDimensions();
        DimensionsText.Text = $"{cols}x{rows}";

        StartConPtySession((short)cols, (short)rows);
    }

    private void StartConPtySession(short cols, short rows)
    {
        try
        {
            StatusText.Text = "Starting...";

            // Create ConPTY with terminal dimensions
            _console = PseudoConsole.Create(cols, rows);

            // Create process host
            _processHost = new ProcessHost(_console);
            _processHost.OnExited += OnProcessExited;

            // Start claude in the hardcoded directory
            _processHost.Start("claude", "", WorkingDir);

            // Start the drain loop to read output into buffer
            _processHost.StartDrainLoop(_buffer!);

            // Start monitoring for process exit
            _processHost.StartExitMonitor();

            ProcessIdText.Text = _processHost.ProcessId.ToString();
            StatusText.Text = "Running";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            MessageBox.Show($"Failed to start ConPTY session:\n\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTerminalInput(byte[] data)
    {
        _processHost?.Write(data);
    }

    private void OnTerminalSizeChanged(short cols, short rows)
    {
        DimensionsText.Text = $"{cols}x{rows}";
        try
        {
            _console?.Resize(cols, rows);
        }
        catch
        {
            // Resize may fail if console is disposed
        }
    }

    private void OnProcessExited(int exitCode)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"Exited ({exitCode})";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 100, 100));
        });
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _terminal?.Detach();

        if (_processHost != null)
        {
            try
            {
                await _processHost.GracefulShutdownAsync(2000);
            }
            catch
            {
                // Ignore shutdown errors
            }
            finally
            {
                _processHost.Dispose();
            }
        }

        _buffer?.Dispose();
    }
}
