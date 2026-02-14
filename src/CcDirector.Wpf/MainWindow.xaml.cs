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
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Wpf.Backends;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private readonly ObservableCollection<PipeMessageViewModel> _pipeMessages = new();
    private const int MaxPipeMessages = 500;

    private bool _pipeMessagesExpanded;
    private bool _updatingScrollBar;
    private SessionManager _sessionManager = null!;
    private TerminalControl? _terminalControl;
    private EmbeddedBackend? _activeEmbeddedBackend;
    private Session? _activeSession;
    private CancellationTokenSource? _enterRetryCts;
    private SessionViewModel? _headerBoundVm;
    private readonly System.Windows.Threading.DispatcherTimer _repoChangeTimer;
    private readonly GitStatusProvider _gitStatusProvider = new();
    private bool _repoChangeRefreshRunning;
    private CancellationTokenSource? _persistDebounceCts;
    private const int PersistDebounceMs = 250;
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
            if (_activeEmbeddedBackend != null &&
                _activeEmbeddedBackend.IsRunning &&
                WindowState != WindowState.Minimized)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    () => _activeEmbeddedBackend?.EnsureZOrder());
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Cancel any pending debounced persist and flush immediately
        _persistDebounceCts?.Cancel();
        PersistSessionStateCore();

        var app = (App)Application.Current;

        // Unsubscribe event handlers to prevent memory leaks
        if (app.EventRouter != null)
            app.EventRouter.OnRawMessage -= OnPipeMessageReceived;
        _sessionManager.OnClaudeSessionRegistered -= OnClaudeSessionRegistered;

        // In sandbox mode, skip exit dialog and just kill all sessions
        if (app.SandboxMode)
        {
            FileLog.Write("[MainWindow] Sandbox mode: closing without dialog");
            DetachTerminal();
            var sessionsSnapshot = _sessions.ToList();
            foreach (var vm in sessionsSnapshot)
            {
                _ = vm.Session.KillAsync();
            }
            base.OnClosing(e);
            return;
        }

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
                    var session = app.SessionManager.GetSession(sessionId);
                    if (session?.Backend is EmbeddedBackend eb)
                        return eb.ConsoleHwnd.ToInt64();
                    return 0;
                });

                DetachTerminal();
                var sessionsSnapshot = _sessions.ToList();
                foreach (var vm in sessionsSnapshot)
                {
                    if (vm.Session.Backend is EmbeddedBackend eb)
                        eb.Detach();
                }

                app.KeepSessionsOnExit = true;
                base.OnClosing(e);
                return;
            }

            // Checkbox checked — kill all (default behavior, falls through below)
        }

        DetachTerminal();
        // Sessions and their backends are disposed by SessionManager

        base.OnClosing(e);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        _sessionManager = app.SessionManager;

        if (app.EventRouter != null)
            app.EventRouter.OnRawMessage += OnPipeMessageReceived;

        // Subscribe to session registration for ClaudeSessionId persistence
        _sessionManager.OnClaudeSessionRegistered += OnClaudeSessionRegistered;

        // Set build info from assembly
        SetBuildInfo();

        // Restore sessions from previous run (crash recovery)
        RestorePersistedSessions();
    }

    private void SetBuildInfo()
    {
        try
        {
            // Get the exe path for single-file apps
            var exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "cc_director.exe");
            if (System.IO.File.Exists(exePath))
            {
                var buildTime = System.IO.File.GetLastWriteTime(exePath);
                BuildInfoText.Text = $"Build: {buildTime:HH:mm:ss}";
                BuildInfoText.ToolTip = $"Built: {buildTime:yyyy-MM-dd HH:mm:ss}\nPath: {exePath}";
            }
            else
            {
                // Fallback to dll
                var dllPath = System.IO.Path.Combine(AppContext.BaseDirectory, "cc_director.dll");
                if (System.IO.File.Exists(dllPath))
                {
                    var buildTime = System.IO.File.GetLastWriteTime(dllPath);
                    BuildInfoText.Text = $"Build: {buildTime:HH:mm:ss}";
                    BuildInfoText.ToolTip = $"Built: {buildTime:yyyy-MM-dd HH:mm:ss}\nPath: {dllPath}";
                }
                else
                {
                    BuildInfoText.Text = $"Build: {DateTime.Now:HH:mm:ss}";
                }
            }
        }
        catch
        {
            BuildInfoText.Text = "Build: unknown";
        }
    }

    /// <summary>
    /// Restore sessions from previous run that have a ClaudeSessionId.
    /// Uses Claude's --resume flag to continue the conversation.
    /// </summary>
    private void RestorePersistedSessions()
    {
        var app = (App)Application.Current;
        var persisted = app.RestoredPersistedData;

        if (persisted == null || persisted.Count == 0)
        {
            FileLog.Write("[MainWindow] RestorePersistedSessions: No sessions to restore");
            return;
        }

        FileLog.Write($"[MainWindow] RestorePersistedSessions: Found {persisted.Count} persisted session(s)");

        int restored = 0;
        foreach (var p in persisted)
        {
            // Skip sessions without ClaudeSessionId - they never fully started
            if (string.IsNullOrEmpty(p.ClaudeSessionId))
            {
                FileLog.Write($"[MainWindow] Skipping session {p.Id}: No ClaudeSessionId");
                continue;
            }

            // Skip if repo path no longer exists
            if (!System.IO.Directory.Exists(p.RepoPath))
            {
                FileLog.Write($"[MainWindow] Skipping session {p.Id}: Repo path not found: {p.RepoPath}");
                continue;
            }

            try
            {
                // Create new session with --resume flag to continue Claude conversation
                var session = _sessionManager.CreateSession(
                    p.RepoPath,
                    claudeArgs: null,
                    SessionBackendType.ConPty,
                    resumeSessionId: p.ClaudeSessionId);

                if (session != null)
                {
                    // Restore custom name and color
                    session.CustomName = p.CustomName;
                    session.CustomColor = p.CustomColor;

                    var vm = new SessionViewModel(session, Dispatcher);
                    vm.PendingPromptText = p.PendingPromptText ?? string.Empty;
                    _sessions.Add(vm);
                    restored++;

                    FileLog.Write($"[MainWindow] Restored session {session.Id} from {p.RepoPath} (Resume={p.ClaudeSessionId[..8]}...)");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] Failed to restore session for {p.RepoPath}: {ex.Message}");
            }
        }

        if (restored > 0)
        {
            FileLog.Write($"[MainWindow] Restored {restored} session(s) from previous run");

            // Clear persisted data after successful restoration to avoid re-restoring
            app.SessionStateStore.Clear();
            app.RestoredPersistedData = null;

            // Select first restored session
            if (_sessions.Count > 0)
            {
                SessionList.SelectedItem = _sessions[0];
            }

            // Persist the newly created sessions
            PersistSessionState();
        }
        else
        {
            // No sessions restored, clear the state store
            app.SessionStateStore.Clear();
            app.RestoredPersistedData = null;
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
            var resumeSessionId = dialog.SelectedResumeSessionId;

            // Create the session (with optional resume)
            var vm = CreateSession(dialog.SelectedPath, resumeSessionId);
            if (vm == null) return;

            // Show rename dialog for new sessions (not resume)
            if (string.IsNullOrEmpty(resumeSessionId))
            {
                ShowRenameDialog(vm);
            }

            PersistSessionState();
        }
    }

    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId = null)
    {
        try
        {
            // Create session with ConPty backend (default mode)
            var session = _sessionManager.CreateSession(repoPath, null, SessionBackendType.ConPty, resumeSessionId);
            var vm = new SessionViewModel(session, Dispatcher);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            PersistSessionState();
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
            // Clear active backend reference if this is the active session
            if (_activeSession?.Id == vm.Session.Id)
            {
                _activeEmbeddedBackend = null;
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

    private void MenuCloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.DataContext is not SessionViewModel vm)
            return;

        var result = MessageBox.Show(this,
            $"Close session \"{vm.DisplayName}\"?\nThis will terminate the process and remove the session.",
            "Close Session", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        FileLog.Write($"[MainWindow] MenuCloseSession_Click: closing session {vm.Session.Id}");

        // Immediately remove from UI
        if (_activeSession?.Id == vm.Session.Id)
        {
            _activeEmbeddedBackend = null;
            DetachTerminal();
        }
        _sessions.Remove(vm);
        PersistSessionState();

        // Background cleanup: kill process and dispose backend
        var sessionId = vm.Session.Id;
        var wasRunning = vm.Session.Status is SessionStatus.Running or SessionStatus.Starting;
        _ = Task.Run(async () =>
        {
            try
            {
                if (wasRunning)
                {
                    await _sessionManager.KillSessionAsync(sessionId);
                }
                _sessionManager.RemoveSession(sessionId);
                FileLog.Write($"[MainWindow] MenuCloseSession_Click: background cleanup complete for {sessionId}");
                // Re-persist now that session is removed from manager;
                // this supersedes the earlier debounced persist that may still see the session.
                _ = Dispatcher.BeginInvoke(PersistSessionState);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] MenuCloseSession_Click background cleanup FAILED: {ex.Message}");
            }
        });
    }

    private void SessionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Suppress all keyboard navigation on the session list — selection is mouse-only
        e.Handled = true;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Save prompt text for outgoing session
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SessionViewModel outgoing)
        {
            outgoing.PendingPromptText = PromptInput.Text;
            FileLog.Write($"[MainWindow] SessionSwitch: saved prompt for {outgoing.Session.Id}");
        }

        if (SessionList.SelectedItem is not SessionViewModel vm)
        {
            DetachTerminal();
            return;
        }

        AttachTerminal(vm.Session);

        // Restore prompt text for incoming session
        PromptInput.Text = vm.PendingPromptText;
        PromptInput.CaretIndex = PromptInput.Text.Length;
        FileLog.Write($"[MainWindow] SessionSwitch: restored prompt for {vm.Session.Id}");

        // Redirect focus back to terminal/prompt so the sidebar doesn't keep focus
        if (_terminalControl != null && SessionTabs.SelectedIndex == 0)
            Dispatcher.BeginInvoke(() => _terminalControl?.Focus());
        else
            Dispatcher.BeginInvoke(() => PromptInput.Focus());
    }

    private void AttachTerminal(Session session)
    {
        _activeSession = session;
        PlaceholderText.Visibility = Visibility.Collapsed;
        SessionTabs.Visibility = Visibility.Visible;
        PromptBar.Visibility = Visibility.Visible;

        // Hide previous embedded backend (don't kill it)
        _activeEmbeddedBackend?.Hide();
        _activeEmbeddedBackend = null;

        // Clean up previous terminal control
        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            _terminalControl = null;
        }
        TerminalArea.Child = null;

        if (session.BackendType == SessionBackendType.Embedded)
        {
            // Get the embedded backend from the session
            if (session.Backend is EmbeddedBackend embeddedBackend)
            {
                _activeEmbeddedBackend = embeddedBackend;
                embeddedBackend.Show();
                DeferConsolePositionUpdate();
            }
        }
        else
        {
            _terminalControl = new TerminalControl();
            TerminalArea.Child = _terminalControl;
            _terminalControl.ScrollChanged += OnTerminalScrollChanged;
            _terminalControl.Attach(session);
            UpdateScrollBar();
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
            _terminalControl.ScrollChanged -= OnTerminalScrollChanged;
            _terminalControl.Detach();
            _terminalControl = null;
        }
        _activeEmbeddedBackend?.Hide();
        _activeEmbeddedBackend = null;
        TerminalArea.Child = null;

        // Reset scrollbar
        TerminalScrollBar.Visibility = Visibility.Collapsed;

        // Hide session header banner
        UpdateSessionHeader();

        GitChanges.Detach();
        SessionTabs.Visibility = Visibility.Collapsed;
        PromptInput.Clear();
        PromptBar.Visibility = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Visible;
    }

    private void TerminalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_terminalControl == null || _updatingScrollBar) return;

        _updatingScrollBar = true;
        int offset = (int)(TerminalScrollBar.Maximum - TerminalScrollBar.Value);
        _terminalControl.ScrollOffset = offset;
        _updatingScrollBar = false;
    }

    private void OnTerminalScrollChanged(object? sender, EventArgs e)
    {
        UpdateScrollBar();
    }

    private void UpdateScrollBar()
    {
        if (_terminalControl == null)
        {
            TerminalScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        _updatingScrollBar = true;
        int total = _terminalControl.ScrollbackCount;
        int viewport = _terminalControl.ViewportRows;

        TerminalScrollBar.Maximum = total;
        TerminalScrollBar.ViewportSize = viewport;
        TerminalScrollBar.LargeChange = viewport;
        TerminalScrollBar.SmallChange = 3;
        TerminalScrollBar.Value = total - _terminalControl.ScrollOffset;

        TerminalScrollBar.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatingScrollBar = false;
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
        UpdateHeaderClaudeMetadata(vm);
        SessionHeaderBanner.Visibility = Visibility.Visible;
        DeferConsolePositionUpdate();
    }

    private void UpdateHeaderClaudeMetadata(SessionViewModel vm)
    {
        if (vm.HasClaudeMetadata)
        {
            HeaderMessageCountBadge.Visibility = Visibility.Visible;
            HeaderMessageCountText.Text = vm.ClaudeMessageCount.ToString();

            var summary = vm.ClaudeSummary ?? vm.ClaudeFirstPrompt;
            if (!string.IsNullOrWhiteSpace(summary))
            {
                // Truncate if too long
                if (summary.Length > 80)
                    summary = summary[..80] + "...";
                HeaderClaudeSummary.Text = summary;
                HeaderClaudeSummary.Visibility = Visibility.Visible;
            }
            else
            {
                HeaderClaudeSummary.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            HeaderMessageCountBadge.Visibility = Visibility.Collapsed;
            HeaderClaudeSummary.Visibility = Visibility.Collapsed;
        }
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
        else if (e.PropertyName is nameof(SessionViewModel.HasClaudeMetadata)
                 or nameof(SessionViewModel.ClaudeMessageCount)
                 or nameof(SessionViewModel.ClaudeSummary)
                 or nameof(SessionViewModel.ClaudeInfoText))
        {
            UpdateHeaderClaudeMetadata(vm);
        }
    }

    private void UpdateConsolePosition()
    {
        if (_activeEmbeddedBackend == null || _activeEmbeddedBackend.ConsoleHwnd == IntPtr.Zero)
            return;

        // TerminalArea is a Border — get its screen-space rect
        if (!TerminalArea.IsVisible || TerminalArea.ActualWidth <= 0 || TerminalArea.ActualHeight <= 0)
            return;

        var topLeft = TerminalArea.PointToScreen(new Point(0, 0));
        var bottomRight = TerminalArea.PointToScreen(new Point(TerminalArea.ActualWidth, TerminalArea.ActualHeight));

        var screenRect = new Rect(topLeft, bottomRight);
        _activeEmbeddedBackend.UpdatePosition(screenRect);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_activeEmbeddedBackend == null) return;

        if (WindowState == WindowState.Minimized)
        {
            _activeEmbeddedBackend.Hide();
        }
        else
        {
            _activeEmbeddedBackend.Show();
            // Reposition after restore
            DeferConsolePositionUpdate();
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_activeEmbeddedBackend != null)
        {
            // Bring console overlay back to top when WPF window regains focus
            _activeEmbeddedBackend.Show();
            DeferConsolePositionUpdate();
        }

        // Ensure focus goes to terminal or prompt, not sidebar
        if (_terminalControl != null && SessionTabs.SelectedIndex == 0)
            Dispatcher.BeginInvoke(() => _terminalControl?.Focus());
        else
            Dispatcher.BeginInvoke(() => PromptInput.Focus());
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_activeEmbeddedBackend == null) return;

        // Don't hide when focus moves to our own embedded console
        var foreground = GetForegroundWindow();
        if (foreground == _activeEmbeddedBackend.ConsoleHwnd) return;

        _activeEmbeddedBackend.Hide();
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
            () => _activeEmbeddedBackend?.EnsureZOrder());
    }

    private void SessionContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (_activeEmbeddedBackend != null && _activeEmbeddedBackend.IsVisible)
        {
            _activeEmbeddedBackend.Show();
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

        if (_activeEmbeddedBackend == null) return;

        // Terminal tab is index 0 — show overlay only when it's selected
        if (SessionTabs.SelectedIndex == 0)
        {
            _activeEmbeddedBackend.Show();
            DeferConsolePositionUpdate();
        }
        else
        {
            _activeEmbeddedBackend.Hide();
        }
    }

    private void BtnRefreshConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEmbeddedBackend != null)
        {
            _activeEmbeddedBackend.Show();
            UpdateConsolePosition();
        }
    }

    private void PromptInput_GotFocus(object sender, RoutedEventArgs e)
    {
        // Re-show console overlay when text box gets focus — covers the case
        // where the user clicked the console window then clicked back here,
        // which can cause the console to slip behind the WPF window.
        if (_activeEmbeddedBackend != null)
        {
            _activeEmbeddedBackend.Show();
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

        // Clear saved prompt text so switching away and back shows empty box
        var activeVm = _sessions.FirstOrDefault(s => s.Session.Id == _activeSession.Id);
        if (activeVm != null)
        {
            activeVm.PendingPromptText = string.Empty;
            FileLog.Write($"[MainWindow] SendPromptAsync: cleared PendingPromptText for {_activeSession.Id}");
        }

        if (_activeSession.BackendType == SessionBackendType.Embedded && _activeEmbeddedBackend != null)
        {
            // Send keystrokes directly to the embedded console window
            await _activeEmbeddedBackend.SendTextAsync(text);
            ScheduleEnterRetry(_activeSession);
        }
        else
        {
            await _activeSession.SendTextAsync(text);
            ScheduleEnterRetry(_activeSession);
        }

        PromptInput.Focus();
    }

    private void ScheduleEnterRetry(Session session)
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
        _ = RetryEnterAfterDelay(session, cts, OnStateChanged);
    }

    private async Task RetryEnterAfterDelay(
        Session session,
        CancellationTokenSource cts,
        Action<ActivityState, ActivityState> handler)
    {
        try
        {
            await Task.Delay(3000, cts.Token);
            await session.SendEnterAsync();
            FileLog.Write("[MainWindow] Enter retry: sent extra Enter (no UserPromptSubmit within 3s)");
        }
        catch (TaskCanceledException) { /* UserPromptSubmit arrived - no retry needed */ }
        finally
        {
            session.OnActivityStateChanged -= handler;
        }
    }

    private void PersistSessionState()
    {
        Debug.Assert(Dispatcher.CheckAccess(), "PersistSessionState must be called on the UI thread");

        // Cancel any pending debounce
        _persistDebounceCts?.Cancel();
        _persistDebounceCts = new CancellationTokenSource();
        var cts = _persistDebounceCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PersistDebounceMs, cts.Token);
                PersistSessionStateCore();
            }
            catch (TaskCanceledException) { /* debounce superseded */ }
        });
    }

    private void PersistSessionStateCore()
    {
        var app = (App)Application.Current;

        // In sandbox mode, don't persist session state
        if (app.SandboxMode)
        {
            return;
        }

        try
        {
            // Sync prompt text from VMs to Session objects so it gets persisted.
            // Must read PromptInput.Text on the UI thread.
            if (Dispatcher.CheckAccess())
            {
                SyncPromptTextToSessions();
            }
            else
            {
                Dispatcher.Invoke(SyncPromptTextToSessions);
            }

            _sessionManager.SaveCurrentState(app.SessionStateStore);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] PersistSessionState error: {ex.Message}");
        }
    }

    /// <summary>Copy prompt text from VMs (and the active PromptInput) to Session objects for persistence. Must run on UI thread.</summary>
    private void SyncPromptTextToSessions()
    {
        FileLog.Write($"[MainWindow] SyncPromptTextToSessions: syncing {_sessions.Count} session(s)");

        foreach (var vm in _sessions)
        {
            vm.Session.PendingPromptText = vm.PendingPromptText;
        }

        // For the active session, capture the live PromptInput text
        if (_activeSession != null)
        {
            var activeVm = _sessions.FirstOrDefault(s => s.Session.Id == _activeSession.Id);
            if (activeVm != null)
            {
                activeVm.Session.PendingPromptText = PromptInput.Text;
            }
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

            // Refresh Claude metadata on Stop events (end of turn - metadata may have updated)
            if (msg.HookEventName == "Stop" && !string.IsNullOrEmpty(msg.SessionId))
            {
                var session = _sessionManager.GetSessionByClaudeId(msg.SessionId);
                if (session != null)
                {
                    var sessionVm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
                    sessionVm?.RefreshClaudeMetadata();
                }
            }
        });
    }

    private void OnClaudeSessionRegistered(Session session, string claudeSessionId)
    {
        // Persist session state so ClaudeSessionId is saved for crash recovery
        FileLog.Write($"[MainWindow] Claude session registered: {claudeSessionId} for {session.RepoPath}");
        // This event can fire from a non-UI thread; PersistSessionState touches _persistDebounceCts
        // which must only be accessed on the UI thread.
        Dispatcher.BeginInvoke(PersistSessionState);
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

    private async void MenuGitHubIssues_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.DataContext is not SessionViewModel vm ||
            string.IsNullOrEmpty(vm.Session.RepoPath) ||
            !System.IO.Directory.Exists(vm.Session.RepoPath))
        {
            return;
        }

        FileLog.Write($"[MainWindow] MenuGitHubIssues_Click: {vm.Session.RepoPath}");

        // Get remote URL
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "remote get-url origin",
            WorkingDirectory = vm.Session.RepoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc is null) return;

        string remoteUrl = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        await proc.WaitForExitAsync();

        if (string.IsNullOrEmpty(remoteUrl)) return;

        // Parse owner/repo from remote URL
        // SSH: git@github.com:owner/repo.git
        // HTTPS: https://github.com/owner/repo.git
        string ownerRepo;
        if (remoteUrl.StartsWith("git@github.com:"))
        {
            ownerRepo = remoteUrl.Substring("git@github.com:".Length);
        }
        else if (remoteUrl.StartsWith("https://github.com/"))
        {
            ownerRepo = remoteUrl.Substring("https://github.com/".Length);
        }
        else
        {
            return;
        }

        if (ownerRepo.EndsWith(".git"))
            ownerRepo = ownerRepo.Substring(0, ownerRepo.Length - 4);

        // Show dialog with URL
        string issuesUrl = $"https://github.com/{ownerRepo}/issues";
        FileLog.Write($"[MainWindow] MenuGitHubIssues_Click: {issuesUrl}");
        var dialog = new GitHubIssuesDialog(issuesUrl) { Owner = this };
        dialog.ShowDialog();
    }

    private void SessionMenuButton_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] SessionMenuButton_Click");
        if (sender is not Button button)
            return;

        // Find the parent Grid that has the ContextMenu
        var parent = VisualTreeHelper.GetParent(button);
        while (parent != null && parent is not Grid)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is Grid grid && grid.ContextMenu != null)
        {
            grid.ContextMenu.PlacementTarget = button;
            grid.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            grid.ContextMenu.IsOpen = true;
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
                        _ = Dispatcher.BeginInvoke(() => repo.UncommittedCount = count);
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

    private async void BtnCreateRepo_Click(object sender, RoutedEventArgs e)
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
            if (process != null)
                await process.WaitForExitAsync();

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
        PersistSessionState();

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
        session.OnClaudeMetadataChanged += OnClaudeMetadataChanged;
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

    /// <summary>Prompt text the user was composing but hasn't sent yet. Saved/restored on session switch.</summary>
    public string PendingPromptText { get; set; } = string.Empty;

    public string StatusText => $"{Session.ActivityState} (PID {Session.ProcessId}) - ConPTY";
    public SolidColorBrush ActivityBrush => ActivityBrushes.GetValueOrDefault(Session.ActivityState, ActivityBrushes[ActivityState.Starting]);

    /// <summary>Claude session summary (from sessions-index.json).</summary>
    public string? ClaudeSummary => Session.ClaudeMetadata?.Summary;

    /// <summary>Claude session message count.</summary>
    public int ClaudeMessageCount => Session.ClaudeMetadata?.MessageCount ?? 0;

    /// <summary>Claude session first prompt (truncated).</summary>
    public string? ClaudeFirstPrompt => Session.ClaudeMetadata?.FirstPrompt;

    /// <summary>Short display text for Claude info: summary or first prompt snippet.</summary>
    public string ClaudeInfoText
    {
        get
        {
            var meta = Session.ClaudeMetadata;
            if (meta == null) return string.Empty;

            // Prefer summary, fall back to first prompt
            var text = !string.IsNullOrWhiteSpace(meta.Summary)
                ? meta.Summary
                : meta.FirstPrompt;

            if (string.IsNullOrEmpty(text)) return string.Empty;

            // Truncate if too long
            const int maxLen = 60;
            if (text.Length > maxLen)
                text = text[..maxLen] + "...";

            return text;
        }
    }

    /// <summary>Whether Claude metadata is available.</summary>
    public bool HasClaudeMetadata => Session.ClaudeMetadata != null;

    /// <summary>Refresh Claude metadata from sessions-index.json.</summary>
    public void RefreshClaudeMetadata()
    {
        Session.RefreshClaudeMetadata();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActivityBrush));
        });
    }

    private void OnClaudeMetadataChanged(ClaudeSessionMetadata? metadata)
    {
        _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ClaudeSummary));
            OnPropertyChanged(nameof(ClaudeMessageCount));
            OnPropertyChanged(nameof(ClaudeFirstPrompt));
            OnPropertyChanged(nameof(ClaudeInfoText));
            OnPropertyChanged(nameof(HasClaudeMetadata));
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
