using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
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
    private CancellationTokenSource? _enterRetryCts;
    private SessionViewModel? _headerBoundVm;
    private readonly System.Windows.Threading.DispatcherTimer _repoChangeTimer;
    private readonly GitStatusProvider _gitStatusProvider = new();
    private bool _repoChangeRefreshRunning;
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
        Deactivated += MainWindow_Deactivated;
        SessionTabs.SelectionChanged += SessionTabs_SelectionChanged;

        _repoChangeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _repoChangeTimer.Tick += async (_, _) => await RefreshRepoChangeCountsAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private const int WM_NCACTIVATE = 0x0086;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_NCACTIVATE fires when the title bar activation state changes
        // (title bar click, alt-tab, etc.). Re-assert console z-order when
        // the window is being activated (wParam != 0).
        if (msg == WM_NCACTIVATE && wParam != IntPtr.Zero)
        {
            if (_activeEmbeddedHost != null &&
                _activeEmbeddedHost.IsVisible &&
                WindowState != WindowState.Minimized)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    () => _activeEmbeddedHost?.EnsureZOrder());
            }
        }

        return IntPtr.Zero;
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
        var recentStore = app.RecentSessionStore;

        var dialog = new NewSessionDialog(registry, recentStore);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            if (!string.IsNullOrWhiteSpace(dialog.SelectedCustomName))
            {
                // Check if session with same name is already open
                var existing = _sessions.FirstOrDefault(s =>
                    string.Equals(s.Session.CustomName, dialog.SelectedCustomName, StringComparison.Ordinal));

                if (existing != null)
                {
                    // Switch to existing session, update recent timestamp
                    SessionList.SelectedItem = existing;
                    recentStore.Add(dialog.SelectedPath, dialog.SelectedCustomName, existing.Session.CustomColor);
                    return;
                }

                // No duplicate — create new session with the recent name
                // Look up color from the recent entry
                var recentColor = LookupRecentColor(dialog.SelectedPath, dialog.SelectedCustomName, recentStore.GetRecent());
                var vm = CreateSession(dialog.SelectedPath);
                if (vm == null) return;
                vm.Rename(dialog.SelectedCustomName, recentColor);
                PersistSessionState();
                recentStore.Add(dialog.SelectedPath, dialog.SelectedCustomName, recentColor);
            }
            else
            {
                // New repo selection — open rename dialog immediately
                var vm = CreateSession(dialog.SelectedPath);
                if (vm == null) return;
                ShowRenameDialog(vm);
                if (!string.IsNullOrWhiteSpace(vm.Session.CustomName))
                    recentStore.Add(dialog.SelectedPath, vm.Session.CustomName, vm.Session.CustomColor);
            }
        }
    }

    private SessionViewModel? CreateSession(string repoPath)
    {
        try
        {
            var session = _sessionManager.CreateEmbeddedSession(repoPath);
            var vm = new SessionViewModel(session, Dispatcher);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            return vm;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to create session:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
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
            PersistSessionState();

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
            PersistSessionState();
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
                // Check if Windows Terminal is the default - warn user before proceeding
                if (EmbeddedConsoleHost.IsWindowsTerminalDefault())
                {
                    var warningDialog = new WindowsTerminalWarningDialog { Owner = this };
                    if (warningDialog.ShowDialog() != true)
                    {
                        // User declined or opened settings - don't start session
                        var sessionMgr = ((App)Application.Current).SessionManager;
                        sessionMgr.RemoveSession(session.Id);
                        SessionList.SelectedItem = null;
                        PlaceholderText.Visibility = Visibility.Visible;
                        SessionTabs.Visibility = Visibility.Collapsed;
                        return;
                    }
                }

                // Create new host
                var app = (App)Application.Current;
                var host = new EmbeddedConsoleHost();
                _activeEmbeddedHost = host;
                _embeddedHosts[session.Id] = host;

                string args = session.ClaudeArgs ?? string.Empty;
                host.StartProcess(app.SessionManager.Options.ClaudePath, args, session.WorkingDirectory);
                session.SetEmbeddedProcessId(host.ProcessId);
                PersistSessionState();

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

        // Show session header banner
        UpdateSessionHeader();

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

        // Hide session header banner
        UpdateSessionHeader();

        GitChanges.Detach();
        SessionTabs.Visibility = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Visible;
    }

    private static readonly Dictionary<ActivityState, string> ActivityLabels = new()
    {
        [ActivityState.Starting] = "Starting",
        [ActivityState.Idle] = "Idle",
        [ActivityState.Working] = "Working",
        [ActivityState.WaitingForInput] = "Your Turn",
        [ActivityState.WaitingForPerm] = "Needs Permission",
        [ActivityState.Exited] = "Exited",
    };

    private void UpdateSessionHeader()
    {
        // Unsubscribe from previous VM
        if (_headerBoundVm != null)
        {
            _headerBoundVm.PropertyChanged -= OnHeaderVmPropertyChanged;
            _headerBoundVm = null;
        }

        if (_activeSession == null)
        {
            SessionHeaderBanner.Visibility = Visibility.Collapsed;
            DeferConsolePositionUpdate();
            return;
        }

        // Find the VM for the active session
        var vm = _sessions.FirstOrDefault(s => s.Session.Id == _activeSession.Id);
        if (vm == null)
        {
            SessionHeaderBanner.Visibility = Visibility.Collapsed;
            DeferConsolePositionUpdate();
            return;
        }

        _headerBoundVm = vm;
        vm.PropertyChanged += OnHeaderVmPropertyChanged;

        HeaderSessionName.Text = vm.DisplayName;
        SessionHeaderBanner.Background = vm.CustomColorBrush;
        UpdateHeaderActivityState(vm);
        SessionHeaderBanner.Visibility = Visibility.Visible;
        DeferConsolePositionUpdate();
    }

    private void UpdateHeaderActivityState(SessionViewModel vm)
    {
        var activityBrush = vm.ActivityBrush;

        // Create semi-transparent version of the activity color for badge background
        var activityColor = activityBrush.Color;
        var badgeBg = new SolidColorBrush(Color.FromArgb(0x33, activityColor.R, activityColor.G, activityColor.B));
        badgeBg.Freeze();
        HeaderStateBadge.Background = badgeBg;

        HeaderStateBadgeText.Text = ActivityLabels.GetValueOrDefault(
            vm.Session.ActivityState, "Starting");

        // Set header font color based on background luminance
        var headerBg = vm.CustomColorBrush.Color;
        var foreground = GetContrastForeground(headerBg);
        HeaderSessionName.Foreground = foreground;
    }

    private static SolidColorBrush GetContrastForeground(Color c)
    {
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance < 0.5 ? Brushes.White : Brushes.Black;
    }

    private void OnHeaderVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SessionViewModel vm) return;

        if (e.PropertyName is nameof(SessionViewModel.ActivityBrush) or nameof(SessionViewModel.StatusText))
        {
            UpdateHeaderActivityState(vm);
        }
        else if (e.PropertyName == nameof(SessionViewModel.DisplayName))
        {
            HeaderSessionName.Text = vm.DisplayName;
        }
        else if (e.PropertyName is nameof(SessionViewModel.CustomColor) or nameof(SessionViewModel.CustomColorBrush))
        {
            SessionHeaderBanner.Background = vm.CustomColorBrush;
            UpdateHeaderActivityState(vm);
        }
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

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_activeEmbeddedHost == null) return;

        // Don't hide when focus moves to our own embedded console
        var foreground = GetForegroundWindow();
        if (foreground == _activeEmbeddedHost.ConsoleHwnd) return;

        _activeEmbeddedHost.Hide();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private void DeferConsolePositionUpdate()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            UpdateConsolePosition);
    }

    private void SessionContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Context menu popups can push the console overlay behind the WPF window.
        // Re-assert z-order after a brief delay so the popup settles first.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () => _activeEmbeddedHost?.EnsureZOrder());
    }

    private void SessionContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (_activeEmbeddedHost != null && _activeEmbeddedHost.IsVisible)
        {
            _activeEmbeddedHost.Show();
            DeferConsolePositionUpdate();
        }
    }

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Refresh repo list when Repositories tab is selected (index 2)
        if (SessionTabs.SelectedIndex == 2)
        {
            RefreshRepoManagerList();
            _ = RefreshRepoChangeCountsAsync();
            _repoChangeTimer.Start();
        }
        else
        {
            _repoChangeTimer.Stop();
        }

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
            ScheduleEnterRetry(_activeSession, _activeEmbeddedHost);
        }
        else
        {
            await _activeSession.SendTextAsync(text);
        }

        PromptInput.Focus();
    }

    private void ScheduleEnterRetry(Session session, EmbeddedConsoleHost host)
    {
        _enterRetryCts?.Cancel();
        _enterRetryCts = new CancellationTokenSource();
        var cts = _enterRetryCts;

        void OnStateChanged(ActivityState oldState, ActivityState newState)
        {
            if (newState == ActivityState.Working)
            {
                cts.Cancel();
                session.OnActivityStateChanged -= OnStateChanged;
            }
        }

        session.OnActivityStateChanged += OnStateChanged;
        _ = RetryEnterAfterDelay(session, host, cts, OnStateChanged);
    }

    private async Task RetryEnterAfterDelay(
        Session session,
        EmbeddedConsoleHost host,
        CancellationTokenSource cts,
        Action<ActivityState, ActivityState> handler)
    {
        try
        {
            await Task.Delay(3000, cts.Token);
            await host.SendEnterAsync();
            FileLog.Write("[MainWindow] Enter retry: sent extra Enter (no UserPromptSubmit within 3s)");
        }
        catch (TaskCanceledException) { /* UserPromptSubmit arrived — no retry needed */ }
        finally
        {
            session.OnActivityStateChanged -= handler;
        }
    }

    private void PersistSessionState()
    {
        try
        {
            var app = (App)Application.Current;
            _sessionManager.SaveCurrentState(app.SessionStateStore);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] PersistSessionState error: {ex.Message}");
        }
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

    private void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var trackedPids = _sessionManager.GetTrackedProcessIds();
            var claudeProcesses = Process.GetProcessesByName("claude");
            var orphans = new List<Process>();

            foreach (var proc in claudeProcesses)
            {
                if (proc.HasExited || trackedPids.Contains(proc.Id))
                {
                    proc.Dispose();
                    continue;
                }
                orphans.Add(proc);
            }

            if (orphans.Count == 0)
            {
                MessageBox.Show(this, "No orphaned claude.exe sessions found.",
                    "Reconnect", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var wpfHwnd = new WindowInteropHelper(this).Handle;
            var app = (App)Application.Current;
            var recentSessions = app.RecentSessionStore.GetRecent();
            int reconnected = 0;

            foreach (var proc in orphans)
            {
                try
                {
                    var host = EmbeddedConsoleHost.Reattach(proc.Id, IntPtr.Zero);
                    if (host == null)
                    {
                        FileLog.Write($"[Reconnect] Could not reattach to PID {proc.Id}, skipping.");
                        continue;
                    }

                    // Try to extract repo path from the console window title
                    string repoPath = ExtractRepoPathFromTitle(
                        EmbeddedConsoleHost.GetWindowTitle(host.ConsoleHwnd));

                    // Look up custom name and color from recent sessions by repo path
                    var recentMatch = LookupRecentSession(repoPath, recentSessions);

                    var ps = new PersistedSession
                    {
                        Id = Guid.NewGuid(),
                        RepoPath = repoPath,
                        WorkingDirectory = repoPath,
                        CustomName = recentMatch?.CustomName,
                        CustomColor = recentMatch?.CustomColor,
                        EmbeddedProcessId = proc.Id,
                        ActivityState = ActivityState.Idle,
                        CreatedAt = DateTimeOffset.UtcNow,
                    };

                    var session = _sessionManager.RestoreEmbeddedSession(ps);
                    var vm = new SessionViewModel(session, Dispatcher);
                    _sessions.Add(vm);

                    _embeddedHosts[session.Id] = host;
                    host.SetOwner(wpfHwnd);
                    host.OnProcessExited += exitCode =>
                    {
                        session.NotifyEmbeddedProcessExited(exitCode);
                    };

                    reconnected++;
                    FileLog.Write($"[Reconnect] Adopted PID {proc.Id} as session {session.Id}, repo=\"{repoPath}\", name=\"{recentMatch?.CustomName ?? "(none)"}\"");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Reconnect] Error adopting PID {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }

            if (reconnected > 0)
                PersistSessionState();

            MessageBox.Show(this,
                reconnected > 0
                    ? $"Reconnected {reconnected} session(s)."
                    : "Found orphaned processes but could not reconnect any.",
                "Reconnect", MessageBoxButton.OK, MessageBoxImage.Information);

            // Auto-select the first session if none is selected
            if (reconnected > 0 && SessionList.SelectedItem == null && _sessions.Count > 0)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    () => SessionList.SelectedItem = _sessions[0]);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Reconnect] Error: {ex.Message}");
            MessageBox.Show(this, $"Reconnect failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Extract a directory path from a console window title. Claude Code typically
    /// shows the working directory in the title (e.g. "D:\ReposFred\cc_director").
    /// Returns "Unknown" if no path can be extracted.
    /// </summary>
    private static string ExtractRepoPathFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "Unknown";

        // Look for a Windows path pattern (drive letter:\...)
        // The title may contain other text, so scan for path-like segments
        var parts = title.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length >= 3 && char.IsLetter(part[0]) && part[1] == ':' && part[2] == '\\')
            {
                // Validate it looks like a real directory path
                var candidate = part.TrimEnd('\\', '/', '>', ' ');
                if (System.IO.Directory.Exists(candidate))
                    return candidate;
            }
        }

        // Fallback: if the entire title is a path
        var trimmed = title.Trim().TrimEnd('\\', '/', '>', ' ');
        if (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && trimmed[2] == '\\')
        {
            if (System.IO.Directory.Exists(trimmed))
                return trimmed;
        }

        return "Unknown";
    }

    /// <summary>
    /// Look up a recent session entry by matching repo path.
    /// Returns the most recently used entry for this path, or null if no match.
    /// </summary>
    private static RecentSession? LookupRecentSession(string repoPath, IReadOnlyList<RecentSession> recentSessions)
    {
        if (repoPath == "Unknown" || recentSessions.Count == 0)
            return null;

        var normalized = System.IO.Path.GetFullPath(repoPath).TrimEnd('\\', '/');
        return recentSessions.FirstOrDefault(r =>
            string.Equals(
                System.IO.Path.GetFullPath(r.RepoPath).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? LookupRecentColor(string repoPath, string? customName, IReadOnlyList<RecentSession> recentSessions)
    {
        if (repoPath == "Unknown" || recentSessions.Count == 0)
            return null;

        var normalized = System.IO.Path.GetFullPath(repoPath).TrimEnd('\\', '/');
        var match = recentSessions.FirstOrDefault(r =>
            string.Equals(System.IO.Path.GetFullPath(r.RepoPath).TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.CustomName, customName, StringComparison.Ordinal));

        return match?.CustomColor;
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logDir = System.IO.Path.GetDirectoryName(FileLog.CurrentLogPath);
        if (logDir != null && System.IO.Directory.Exists(logDir))
            Process.Start("explorer.exe", logDir);
        else
            MessageBox.Show(this, $"Log directory not found:\n{logDir}", "Logs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void MenuRenameSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem &&
            menuItem.DataContext is SessionViewModel vm)
        {
            ShowRenameDialog(vm);
        }
    }

    private void MenuOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem &&
            menuItem.DataContext is SessionViewModel vm &&
            !string.IsNullOrEmpty(vm.Session.RepoPath) &&
            System.IO.Directory.Exists(vm.Session.RepoPath))
        {
            Process.Start("explorer.exe", vm.Session.RepoPath);
        }
    }

    private void MenuOpenInVsCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem &&
            menuItem.DataContext is SessionViewModel vm &&
            !string.IsNullOrEmpty(vm.Session.RepoPath) &&
            System.IO.Directory.Exists(vm.Session.RepoPath))
        {
            Process.Start(new ProcessStartInfo("code", vm.Session.RepoPath)
            {
                UseShellExecute = true,
            });
        }
    }

    // --- Repository Management Tab ---

    private void RefreshRepoManagerList()
    {
        var app = (App)Application.Current;
        RepoManagerList.ItemsSource = app.RepositoryRegistry.Repositories
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task RefreshRepoChangeCountsAsync()
    {
        if (_repoChangeRefreshRunning) return;
        _repoChangeRefreshRunning = true;

        try
        {
            var app = (App)Application.Current;
            var repos = app.RepositoryRegistry.Repositories.ToList();

            using var semaphore = new SemaphoreSlim(4);
            var tasks = repos.Select(async repo =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (!System.IO.Directory.Exists(repo.Path)) return;
                    var result = await _gitStatusProvider.GetStatusAsync(repo.Path);
                    if (result.Success)
                    {
                        int count = result.StagedChanges.Count + result.UnstagedChanges.Count;
                        Dispatcher.BeginInvoke(() => repo.UncommittedCount = count);
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[RepoChanges] Error checking {repo.Path}: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoChanges] RefreshRepoChangeCounts error: {ex.Message}");
        }
        finally
        {
            _repoChangeRefreshRunning = false;
        }
    }

    private async void BtnCloneRepo_Click(object sender, RoutedEventArgs e)
    {
        var urlDialog = new CloneRepoDialog { Owner = this };
        if (urlDialog.ShowDialog() != true) return;

        var url = urlDialog.RepoUrl;
        var destination = urlDialog.Destination;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(destination))
            return;

        try
        {
            var psi = new ProcessStartInfo("git", $"clone \"{url}\" \"{destination}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                MessageBox.Show(this, "Failed to start git.", "Clone Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await process.WaitForExitAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            if (process.ExitCode != 0)
            {
                MessageBox.Show(this, $"git clone failed:\n{stderr}", "Clone Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var app = (App)Application.Current;
            app.RepositoryRegistry.TryAdd(destination);
            RefreshRepoManagerList();
            _ = RefreshRepoChangeCountsAsync();

            MessageBox.Show(this, $"Repository cloned to:\n{destination}", "Clone Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Clone failed:\n{ex.Message}", "Clone Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnAddExistingRepo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Repository Folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            var app = (App)Application.Current;
            if (app.RepositoryRegistry.TryAdd(dialog.FolderName))
            {
                RefreshRepoManagerList();
                _ = RefreshRepoChangeCountsAsync();
            }
            else
                MessageBox.Show(this, "Repository is already registered.", "Add Repository",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnCreateRepo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Folder for New Repository"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var psi = new ProcessStartInfo("git", $"init \"{dialog.FolderName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = Process.Start(psi);
            process?.WaitForExit();

            if (process?.ExitCode != 0)
            {
                MessageBox.Show(this, "git init failed.", "Create Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var app = (App)Application.Current;
            app.RepositoryRegistry.TryAdd(dialog.FolderName);
            RefreshRepoManagerList();
            _ = RefreshRepoChangeCountsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Create failed:\n{ex.Message}", "Create Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnLaunchSessionFromRepo_Click(object sender, RoutedEventArgs e)
    {
        if (RepoManagerList.SelectedItem is not RepositoryConfig repo)
        {
            MessageBox.Show(this, "Select a repository first.", "Launch Session",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!System.IO.Directory.Exists(repo.Path))
        {
            MessageBox.Show(this, $"Repository path not found:\n{repo.Path}", "Launch Session",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var vm = CreateSession(repo.Path);
        if (vm == null) return;

        ShowRenameDialog(vm);
        if (!string.IsNullOrWhiteSpace(vm.Session.CustomName))
        {
            var app = (App)Application.Current;
            app.RecentSessionStore.Add(repo.Path, vm.Session.CustomName, vm.Session.CustomColor);
        }

        // Switch to Terminal tab to show the new session
        SessionTabs.SelectedIndex = 0;
    }

    private void BtnRepoOpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (RepoManagerList.SelectedItem is RepositoryConfig repo &&
            System.IO.Directory.Exists(repo.Path))
        {
            Process.Start("explorer.exe", repo.Path);
        }
    }

    private void BtnRemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (RepoManagerList.SelectedItem is not RepositoryConfig repo)
        {
            MessageBox.Show(this, "Select a repository first.", "Remove Repository",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            $"Remove \"{repo.Name}\" from the registry?\n\nThis does NOT delete files on disk.",
            "Remove Repository", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var app = (App)Application.Current;
        app.RepositoryRegistry.Remove(repo.Path);
        RefreshRepoManagerList();
        _ = RefreshRepoChangeCountsAsync();
    }

    private void ShowRenameDialog(SessionViewModel vm)
    {
        var dialog = new RenameSessionDialog(vm.DisplayName, vm.Session.CustomColor) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            vm.Rename(dialog.SessionName, dialog.SelectedColor);
            PersistSessionState();
        }
    }

    // --- Drag-and-drop session reordering ---

    private Point _dragStartPoint;
    private DropInsertionAdorner? _dropAdorner;

    private void SessionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void SessionList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Find the ListBoxItem under the mouse
        if (GetSessionViewModelAtPoint(e.GetPosition(SessionList)) is not SessionViewModel draggedVm)
            return;

        var data = new DataObject("SessionViewModel", draggedVm);
        DragDrop.DoDragDrop(SessionList, data, DragDropEffects.Move);
        RemoveDropAdorner();
    }

    private void SessionList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SessionViewModel"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // Show insertion indicator
        var pos = e.GetPosition(SessionList);
        var (targetIndex, insertBelow) = GetDropTargetIndex(pos);

        if (targetIndex >= 0 && targetIndex < _sessions.Count)
        {
            var container = (ListBoxItem)SessionList.ItemContainerGenerator.ContainerFromIndex(targetIndex);
            if (container != null)
                ShowDropAdorner(container, insertBelow);
            else
                RemoveDropAdorner();
        }
        else
        {
            RemoveDropAdorner();
        }
    }

    private void SessionList_DragLeave(object sender, DragEventArgs e)
    {
        RemoveDropAdorner();
    }

    private void SessionList_Drop(object sender, DragEventArgs e)
    {
        RemoveDropAdorner();

        if (!e.Data.GetDataPresent("SessionViewModel")) return;
        var draggedVm = (SessionViewModel)e.Data.GetData("SessionViewModel");

        var pos = e.GetPosition(SessionList);
        var (targetIndex, insertBelow) = GetDropTargetIndex(pos);

        int fromIndex = _sessions.IndexOf(draggedVm);
        if (fromIndex < 0) return;

        int toIndex = insertBelow ? targetIndex + 1 : targetIndex;
        // Clamp
        toIndex = Math.Max(0, Math.Min(toIndex, _sessions.Count - 1));

        // Adjust for removal shift
        if (fromIndex < toIndex)
            toIndex--;

        if (fromIndex != toIndex && toIndex >= 0 && toIndex < _sessions.Count)
        {
            _sessions.Move(fromIndex, toIndex);
            SessionList.SelectedItem = draggedVm;
            PersistSessionState();
        }
    }

    private SessionViewModel? GetSessionViewModelAtPoint(Point point)
    {
        var element = SessionList.InputHitTest(point) as DependencyObject;
        while (element != null && element != SessionList)
        {
            if (element is ListBoxItem item)
                return item.DataContext as SessionViewModel;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private (int index, bool insertBelow) GetDropTargetIndex(Point pos)
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            var container = (ListBoxItem?)SessionList.ItemContainerGenerator.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), SessionList);
            var itemRect = new Rect(itemPos, new Size(container.ActualWidth, container.ActualHeight));

            if (pos.Y >= itemRect.Top && pos.Y <= itemRect.Bottom)
            {
                bool below = pos.Y > itemRect.Top + itemRect.Height / 2;
                return (i, below);
            }
        }

        // If below all items, insert at end
        if (_sessions.Count > 0)
            return (_sessions.Count - 1, true);

        return (-1, false);
    }

    private void ShowDropAdorner(ListBoxItem container, bool below)
    {
        var layer = AdornerLayer.GetAdornerLayer(container);
        if (layer == null) return;

        if (_dropAdorner != null && _dropAdorner.AdornedElement == container && _dropAdorner.IsBelow == below)
            return; // already showing correct adorner

        RemoveDropAdorner();
        _dropAdorner = new DropInsertionAdorner(container, below);
        layer.Add(_dropAdorner);
    }

    private void RemoveDropAdorner()
    {
        if (_dropAdorner == null) return;

        var layer = AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement);
        layer?.Remove(_dropAdorner);
        _dropAdorner = null;
    }
}

internal class DropInsertionAdorner : Adorner
{
    private static readonly Pen LinePen;

    static DropInsertionAdorner()
    {
        LinePen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)), 2);
        LinePen.Freeze();
    }

    public bool IsBelow { get; }

    public DropInsertionAdorner(UIElement adornedElement, bool isBelow)
        : base(adornedElement)
    {
        IsBelow = isBelow;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(AdornedElement.RenderSize);
        double y = IsBelow ? rect.Bottom : rect.Top;
        drawingContext.DrawLine(LinePen, new Point(rect.Left, y), new Point(rect.Right, y));
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

    private static readonly SolidColorBrush DefaultHeaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));

    public string DisplayName => !string.IsNullOrWhiteSpace(Session.CustomName)
        ? Session.CustomName
        : System.IO.Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));

    public string? CustomColor
    {
        get => Session.CustomColor;
        set
        {
            Session.CustomColor = value;
            _customColorBrush = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomColorBrush));
        }
    }

    private SolidColorBrush? _customColorBrush;
    public SolidColorBrush CustomColorBrush
    {
        get
        {
            if (_customColorBrush != null) return _customColorBrush;
            if (!string.IsNullOrWhiteSpace(Session.CustomColor))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(Session.CustomColor);
                    _customColorBrush = Freeze(new SolidColorBrush(color));
                    return _customColorBrush;
                }
                catch { /* fall through to default */ }
            }
            return DefaultHeaderBrush;
        }
    }

    public void Rename(string? newName, string? color = null)
    {
        Session.CustomName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        OnPropertyChanged(nameof(DisplayName));

        // Update color
        CustomColor = color;
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
