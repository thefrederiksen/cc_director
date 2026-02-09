using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private readonly ObservableCollection<PipeMessageViewModel> _pipeMessages = new();
    private const int MaxPipeMessages = 500;

    private bool _pipeMessagesExpanded;
    private SessionManager _sessionManager = null!;
    private TerminalControl? _terminalControl;
    private readonly Dictionary<Guid, EmbeddedConsoleHost> _embeddedHosts = new();
    private EmbeddedConsoleHost? _activeEmbeddedHost;
    private Session? _activeSession;

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        PipeMessageList.ItemsSource = _pipeMessages;
        Loaded += MainWindow_Loaded;
        LocationChanged += (_, _) => DeferConsolePositionUpdate();
        SizeChanged += (_, _) => DeferConsolePositionUpdate();
        StateChanged += MainWindow_StateChanged;
        Activated += MainWindow_Activated;
        SessionTabs.SelectionChanged += SessionTabs_SelectionChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;

        // Check for active running sessions
        var activeSessions = _sessions
            .Where(vm => vm.Session.Status is SessionStatus.Running or SessionStatus.Starting)
            .ToList();

        if (activeSessions.Count > 0)
        {
            var dialog = new CloseDialog(activeSessions.Count) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                e.Cancel = true;
                return;
            }

            if (!dialog.ShutDownCommandWindows)
            {
                // Keep sessions alive — save state, detach consoles
                app.SessionManager.SaveSessionState(app.SessionStateStore, sessionId =>
                {
                    if (_embeddedHosts.TryGetValue(sessionId, out var host))
                        return host.ConsoleHwnd.ToInt64();
                    return 0;
                });

                DetachTerminal();
                foreach (var host in _embeddedHosts.Values)
                    host.Detach();
                _embeddedHosts.Clear();

                app.KeepSessionsOnExit = true;
                base.OnClosing(e);
                return;
            }

            // Checkbox checked — kill all (default behavior, falls through below)
        }

        DetachTerminal();
        foreach (var host in _embeddedHosts.Values)
            host.Dispose();
        _embeddedHosts.Clear();

        base.OnClosing(e);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        _sessionManager = app.SessionManager;

        if (app.EventRouter != null)
            app.EventRouter.OnRawMessage += OnPipeMessageReceived;

        // Restore persisted sessions (loaded by App.OnStartup into SessionManager)
        RestorePersistedSessions();
    }

    private void RestorePersistedSessions()
    {
        var app = (App)Application.Current;
        var persistedData = app.RestoredPersistedData;
        app.RestoredPersistedData = null; // consume once

        var restoredSessions = _sessionManager.ListSessions()
            .Where(s => s.Mode == SessionMode.Embedded && s.Status == SessionStatus.Running)
            .ToList();

        if (restoredSessions.Count == 0) return;

        // Build HWND lookup from persisted data
        var hwndMap = persistedData?.ToDictionary(p => p.Id, p => new IntPtr(p.ConsoleHwnd))
            ?? new Dictionary<Guid, IntPtr>();

        var wpfHwnd = new WindowInteropHelper(this).Handle;

        foreach (var session in restoredSessions)
        {
            var vm = new SessionViewModel(session, Dispatcher);
            _sessions.Add(vm);

            // Reattach the embedded console host using persisted HWND
            hwndMap.TryGetValue(session.Id, out var persistedHwnd);
            var host = EmbeddedConsoleHost.Reattach(session.EmbeddedProcessId, persistedHwnd);
            if (host != null)
            {
                _embeddedHosts[session.Id] = host;
                host.SetOwner(wpfHwnd);
                host.OnProcessExited += exitCode =>
                {
                    session.NotifyEmbeddedProcessExited(exitCode);
                };
            }
        }

        // Auto-select the first session after layout completes so the
        // console overlay positions correctly over the rendered TerminalArea.
        if (_sessions.Count > 0)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                () => SessionList.SelectedItem = _sessions[0]);
        }
    }

    private void BtnNewSession_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var registry = app.RepositoryRegistry;

        var dialog = new NewSessionDialog(registry);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            CreateSession(dialog.SelectedPath);
        }
    }

    private void CreateSession(string repoPath)
    {
        try
        {
            var session = _sessionManager.CreateEmbeddedSession(repoPath);
            var vm = new SessionViewModel(session, Dispatcher);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to create session:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnKillSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel vm)
            return;

        try
        {
            // Dispose and remove the embedded host for this session
            if (_embeddedHosts.Remove(vm.Session.Id, out var host))
            {
                if (_activeEmbeddedHost == host)
                    _activeEmbeddedHost = null;
                host.Dispose();
            }

            await _sessionManager.KillSessionAsync(vm.Session.Id);

            // Detach terminal if this was the active session
            if (_activeSession?.Id == vm.Session.Id)
            {
                DetachTerminal();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to kill session:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuCloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not SessionViewModel vm)
            return;

        var result = MessageBox.Show(this,
            $"Close session \"{vm.DisplayName}\"?\nThis will terminate the process and remove the session.",
            "Close Session", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            // Dispose and remove the embedded host for this session
            if (_embeddedHosts.Remove(vm.Session.Id, out var host))
            {
                if (_activeEmbeddedHost == host)
                    _activeEmbeddedHost = null;
                host.Dispose();
            }

            // Kill if still running
            if (vm.Session.Status is SessionStatus.Running or SessionStatus.Starting)
            {
                await _sessionManager.KillSessionAsync(vm.Session.Id);
            }

            // Detach if active
            if (_activeSession?.Id == vm.Session.Id)
            {
                DetachTerminal();
            }

            // Remove from UI and manager
            _sessions.Remove(vm);
            _sessionManager.RemoveSession(vm.Session.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to close session:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel vm)
        {
            DetachTerminal();
            return;
        }

        AttachTerminal(vm.Session);
    }

    private void AttachTerminal(Session session)
    {
        _activeSession = session;
        PlaceholderText.Visibility = Visibility.Collapsed;
        SessionTabs.Visibility = Visibility.Visible;

        // Hide previous embedded host (don't kill it)
        _activeEmbeddedHost?.Hide();
        _activeEmbeddedHost = null;

        // Clean up previous terminal control
        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            _terminalControl = null;
        }
        TerminalArea.Child = null;

        if (session.Mode == SessionMode.Embedded)
        {
            if (_embeddedHosts.TryGetValue(session.Id, out var existingHost))
            {
                // Reuse existing host — just show it
                _activeEmbeddedHost = existingHost;
                existingHost.Show();
                DeferConsolePositionUpdate();
            }
            else
            {
                // Create new host
                var app = (App)Application.Current;
                var host = new EmbeddedConsoleHost();
                _activeEmbeddedHost = host;
                _embeddedHosts[session.Id] = host;

                string args = session.ClaudeArgs ?? string.Empty;
                host.StartProcess(app.SessionManager.Options.ClaudePath, args, session.WorkingDirectory);
                session.SetEmbeddedProcessId(host.ProcessId);

                // Set WPF window as owner so console stays above it in Z-order
                var wpfHwnd = new WindowInteropHelper(this).Handle;
                host.SetOwner(wpfHwnd);

                host.OnProcessExited += exitCode =>
                {
                    session.NotifyEmbeddedProcessExited(exitCode);
                };

                // Position console overlay after layout
                DeferConsolePositionUpdate();
            }
        }
        else
        {
            _terminalControl = new TerminalControl();
            TerminalArea.Child = _terminalControl;
            _terminalControl.Attach(session);
        }

        // Attach git changes polling
        GitChanges.Attach(session.RepoPath);

        // Select Terminal tab and focus
        SessionTabs.SelectedIndex = 0;
    }

    private void DetachTerminal()
    {
        _activeSession = null;
        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            _terminalControl = null;
        }
        _activeEmbeddedHost?.Hide();
        _activeEmbeddedHost = null;
        TerminalArea.Child = null;

        GitChanges.Detach();
        SessionTabs.Visibility = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Visible;
    }

    private void UpdateConsolePosition()
    {
        if (_activeEmbeddedHost == null || _activeEmbeddedHost.ConsoleHwnd == IntPtr.Zero)
            return;

        // TerminalArea is a Border — get its screen-space rect
        if (!TerminalArea.IsVisible || TerminalArea.ActualWidth <= 0 || TerminalArea.ActualHeight <= 0)
            return;

        var topLeft = TerminalArea.PointToScreen(new Point(0, 0));
        var bottomRight = TerminalArea.PointToScreen(new Point(TerminalArea.ActualWidth, TerminalArea.ActualHeight));

        var screenRect = new Rect(topLeft, bottomRight);
        _activeEmbeddedHost.UpdatePosition(screenRect);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_activeEmbeddedHost == null) return;

        if (WindowState == WindowState.Minimized)
        {
            _activeEmbeddedHost.Hide();
        }
        else
        {
            _activeEmbeddedHost.Show();
            // Reposition after restore
            DeferConsolePositionUpdate();
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_activeEmbeddedHost == null) return;

        // Bring console overlay back to top when WPF window regains focus
        _activeEmbeddedHost.Show();
        DeferConsolePositionUpdate();
    }

    private void DeferConsolePositionUpdate()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            UpdateConsolePosition);
    }

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeEmbeddedHost == null) return;

        // Terminal tab is index 0 — show overlay only when it's selected
        if (SessionTabs.SelectedIndex == 0)
        {
            _activeEmbeddedHost.Show();
            DeferConsolePositionUpdate();
        }
        else
        {
            _activeEmbeddedHost.Hide();
        }
    }

    private void BtnRefreshConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEmbeddedHost != null)
        {
            _activeEmbeddedHost.Show();
            UpdateConsolePosition();
        }
    }

    private void PromptInput_GotFocus(object sender, RoutedEventArgs e)
    {
        // Re-show console overlay when text box gets focus — covers the case
        // where the user clicked the console window then clicked back here,
        // which can cause the console to slip behind the WPF window.
        if (_activeEmbeddedHost != null)
        {
            _activeEmbeddedHost.Show();
            DeferConsolePositionUpdate();
        }
    }

    private async void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private async void BtnSendPrompt_Click(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync();
    }

    private async Task SendPromptAsync()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text))
            return;

        // Strip newlines — Claude Code prompt expects single-line input
        var text = PromptInput.Text.ReplaceLineEndings(" ").Trim();
        PromptInput.Clear();

        if (_activeSession.Mode == SessionMode.Embedded && _activeEmbeddedHost != null)
        {
            // Send keystrokes directly to the embedded console window
            await _activeEmbeddedHost.SendTextAsync(text);
        }
        else
        {
            await _activeSession.SendTextAsync(text);
        }

        PromptInput.Focus();
    }

    private void OnPipeMessageReceived(PipeMessage msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var vm = new PipeMessageViewModel(msg);
            _pipeMessages.Add(vm);

            // FIFO: remove oldest if over limit
            while (_pipeMessages.Count > MaxPipeMessages)
                _pipeMessages.RemoveAt(0);

            // Auto-scroll to bottom
            if (_pipeMessages.Count > 0)
                PipeMessageList.ScrollIntoView(_pipeMessages[^1]);
        });
    }

    private void BtnClearPipeMessages_Click(object sender, RoutedEventArgs e)
    {
        _pipeMessages.Clear();
    }

    private void TogglePipeMessages_Click(object sender, RoutedEventArgs e)
    {
        _pipeMessagesExpanded = !_pipeMessagesExpanded;
        if (_pipeMessagesExpanded)
        {
            PipeMessagesColumn.Width = new GridLength(280);
            PipeMessagesPanel.Visibility = Visibility.Visible;
            PipeToggleButton.Content = "\u00BB";
        }
        else
        {
            PipeMessagesColumn.Width = new GridLength(0);
            PipeMessagesPanel.Visibility = Visibility.Collapsed;
            PipeToggleButton.Content = "\u00AB";
        }
        DeferConsolePositionUpdate();
    }

    private void PromptArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DeferConsolePositionUpdate();
    }

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SessionViewModel vm)
            ShowRenameDialog(vm);
    }

    private void MenuRenameSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem &&
            menuItem.DataContext is SessionViewModel vm)
        {
            ShowRenameDialog(vm);
        }
    }

    private void ShowRenameDialog(SessionViewModel vm)
    {
        var dialog = new RenameSessionDialog(vm.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            vm.Rename(dialog.SessionName);
        }
    }
}

public class SessionViewModel : INotifyPropertyChanged
{
    private static readonly Dictionary<ActivityState, SolidColorBrush> ActivityBrushes = new()
    {
        [ActivityState.Starting] = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))),
        [ActivityState.Idle] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))),
        [ActivityState.Working] = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))),
        [ActivityState.WaitingForInput] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))), // Green (user's turn)
        [ActivityState.WaitingForPerm] = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))),
        [ActivityState.Exited] = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51))),
    };

    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public Session Session { get; }

    public SessionViewModel(Session session, System.Windows.Threading.Dispatcher dispatcher)
    {
        Session = session;
        _dispatcher = dispatcher;
        session.OnActivityStateChanged += OnActivityStateChanged;
    }

    public string DisplayName => !string.IsNullOrWhiteSpace(Session.CustomName)
        ? Session.CustomName
        : System.IO.Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));

    public void Rename(string? newName)
    {
        Session.CustomName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        OnPropertyChanged(nameof(DisplayName));
    }
    public string StatusText => $"{Session.ActivityState} (PID {Session.ProcessId})";
    public SolidColorBrush ActivityBrush => ActivityBrushes.GetValueOrDefault(Session.ActivityState, ActivityBrushes[ActivityState.Starting]);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActivityBrush));
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public class PipeMessageViewModel
{
    private static readonly Dictionary<string, SolidColorBrush> EventBrushes = new()
    {
        ["Stop"] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))),
        ["Notification"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))),
        ["UserPromptSubmit"] = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))),
        ["SessionStart"] = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))),
        ["SessionEnd"] = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))),
    };

    private static readonly SolidColorBrush DefaultEventBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));

    public string Timestamp { get; }
    public string SessionIdShort { get; }
    public string EventName { get; }
    public string Detail { get; }
    public SolidColorBrush EventBrush { get; }

    public PipeMessageViewModel(PipeMessage msg)
    {
        Timestamp = msg.ReceivedAt.ToLocalTime().ToString("HH:mm:ss");
        SessionIdShort = msg.SessionId?.Length >= 8 ? msg.SessionId[..8] : msg.SessionId ?? "unknown";
        EventName = msg.HookEventName ?? "unknown";
        EventBrush = EventBrushes.GetValueOrDefault(EventName, DefaultEventBrush);

        Detail = EventName switch
        {
            "Notification" => msg.NotificationType ?? msg.Message ?? "",
            _ when !string.IsNullOrEmpty(msg.ToolName) => msg.ToolName,
            _ when !string.IsNullOrEmpty(msg.Message) => msg.Message,
            _ => ""
        };
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
