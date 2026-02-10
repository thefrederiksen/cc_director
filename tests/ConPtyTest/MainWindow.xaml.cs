using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ConPtyTest;

public partial class MainWindow : Window
{
    private const int SessionCount = 3;
    private readonly ClaudeSession[] _sessions = new ClaudeSession[SessionCount];
    private int _activeSession = 0;

    // Hardcoded working directory
    private const string WorkingDir = @"D:\ReposFred\cc_director";

    // UI elements for each session
    private Border[] _sessionBorders = null!;
    private TextBlock[] _sessionStatusTexts = null!;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Cache UI element arrays
        _sessionBorders = new[] { Session1Border, Session2Border, Session3Border };
        _sessionStatusTexts = new[] { Session1Status, Session2Status, Session3Status };

        // Create all sessions
        for (int i = 0; i < SessionCount; i++)
        {
            _sessions[i] = new ClaudeSession(i + 1);
            _sessions[i].CreateTerminal();
            _sessions[i].StatusChanged += OnSessionStatusChanged;
            _sessions[i].ProcessExited += OnSessionExited;

            // Add terminal to grid
            if (_sessions[i].Terminal != null)
            {
                TerminalGrid.Children.Add(_sessions[i].Terminal);
            }
        }

        // Show first terminal
        if (_sessions[0].Terminal != null)
        {
            _sessions[0].Terminal!.Visibility = Visibility.Visible;
        }

        // Wire up terminal events for active session input/resize handling
        for (int i = 0; i < SessionCount; i++)
        {
            int sessionIndex = i; // Capture for closure
            _sessions[i].WireTerminalEvents(
                data => OnTerminalInput(sessionIndex, data),
                (cols, rows) => OnTerminalSizeChanged(sessionIndex, cols, rows)
            );
        }

        // Get initial dimensions from first terminal
        var (cols, rows) = _sessions[0].GetDimensions();
        DimensionsText.Text = $"{cols}x{rows}";

        // Start all sessions
        for (int i = 0; i < SessionCount; i++)
        {
            try
            {
                _sessions[i].Start((short)cols, (short)rows, WorkingDir);
            }
            catch (Exception ex)
            {
                _sessionStatusTexts[i].Text = "Error";
                MessageBox.Show($"Failed to start session {i + 1}:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Update UI
        UpdateActiveSessionUI();
        UpdateProcessIdDisplay();

        // Auto-focus the input box
        InputBox.Focus();
    }

    private void SwitchSession(int index)
    {
        if (index == _activeSession || index < 0 || index >= SessionCount)
            return;

        // Hide current terminal
        if (_sessions[_activeSession].Terminal != null)
        {
            _sessions[_activeSession].Terminal!.Visibility = Visibility.Collapsed;
        }

        // Show new terminal
        _activeSession = index;
        if (_sessions[_activeSession].Terminal != null)
        {
            _sessions[_activeSession].Terminal!.Visibility = Visibility.Visible;
        }

        // Update UI
        UpdateActiveSessionUI();
        UpdateProcessIdDisplay();

        // Update dimensions display for active session
        var (cols, rows) = _sessions[_activeSession].GetDimensions();
        DimensionsText.Text = $"{cols}x{rows}";
    }

    private void UpdateActiveSessionUI()
    {
        for (int i = 0; i < SessionCount; i++)
        {
            if (i == _activeSession)
            {
                _sessionBorders[i].Background = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            }
            else
            {
                _sessionBorders[i].Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));
            }
        }

        ActiveSessionText.Text = $"Session {_activeSession + 1}";
    }

    private void UpdateProcessIdDisplay()
    {
        var pid = _sessions[_activeSession].ProcessId;
        ProcessIdText.Text = pid > 0 ? pid.ToString() : "-";
    }

    private void OnSessionStatusChanged(ClaudeSession session, string status)
    {
        Dispatcher.Invoke(() =>
        {
            int index = session.SessionId - 1;
            if (index >= 0 && index < SessionCount)
            {
                _sessionStatusTexts[index].Text = status;

                // Update process ID if this is the active session
                if (index == _activeSession)
                {
                    UpdateProcessIdDisplay();
                }
            }
        });
    }

    private void OnSessionExited(ClaudeSession session, int exitCode)
    {
        Dispatcher.Invoke(() =>
        {
            int index = session.SessionId - 1;
            if (index >= 0 && index < SessionCount)
            {
                _sessionStatusTexts[index].Foreground = new SolidColorBrush(
                    Color.FromRgb(255, 100, 100));
            }
        });
    }

    private void OnTerminalInput(int sessionIndex, byte[] data)
    {
        // Only process input for active session
        if (sessionIndex == _activeSession)
        {
            _sessions[sessionIndex].Write(data);
        }
    }

    private void OnTerminalSizeChanged(int sessionIndex, short cols, short rows)
    {
        // Update dimensions display if this is the active session
        if (sessionIndex == _activeSession)
        {
            DimensionsText.Text = $"{cols}x{rows}";
        }

        // Resize the console for this session
        _sessions[sessionIndex].Resize(cols, rows);
    }

    private void Session1_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchSession(0);
    }

    private void Session2_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchSession(1);
    }

    private void Session3_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchSession(2);
    }

    private async void SendInput()
    {
        var text = InputBox.Text;
        if (string.IsNullOrEmpty(text))
            return;

        // Clear the input box
        InputBox.Clear();

        // Send the text to active session
        var bytes = Encoding.UTF8.GetBytes(text);
        _sessions[_activeSession].Write(bytes);

        // Wait for Claude to process the paste
        await Task.Delay(100);

        // Now send Enter
        _sessions[_activeSession].Write(new byte[] { 0x0D });
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendInput();
        InputBox.Focus();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendInput();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Shutdown all sessions
        var shutdownTasks = new List<Task>();
        foreach (var session in _sessions)
        {
            if (session != null)
            {
                shutdownTasks.Add(session.GracefulShutdownAsync(2000));
            }
        }

        if (shutdownTasks.Count > 0)
        {
            await Task.WhenAll(shutdownTasks);
        }

        // Dispose all sessions
        foreach (var session in _sessions)
        {
            session?.Dispose();
        }
    }
}
