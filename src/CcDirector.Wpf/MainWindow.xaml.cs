using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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

    private SessionManager _sessionManager = null!;
    private TerminalControl? _terminalControl;
    private Session? _activeSession;

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        PipeMessageList.ItemsSource = _pipeMessages;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        _sessionManager = app.SessionManager;

        if (app.EventRouter != null)
            app.EventRouter.OnRawMessage += OnPipeMessageReceived;
    }

    private void BtnNewSession_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var repos = app.Repositories;

        if (repos.Count == 0)
        {
            // No repos configured - ask for a path
            var dialog = new NewSessionDialog();
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                CreateSession(dialog.SelectedPath);
            }
            return;
        }

        // Show repo picker
        var pickerDialog = new NewSessionDialog(repos);
        pickerDialog.Owner = this;
        if (pickerDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(pickerDialog.SelectedPath))
        {
            CreateSession(pickerDialog.SelectedPath);
        }
    }

    private void CreateSession(string repoPath)
    {
        try
        {
            var session = _sessionManager.CreateSession(repoPath);
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
            await _sessionManager.KillSessionAsync(vm.Session.Id);

            // Detach terminal
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

        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            TerminalArea.Child = null;
        }

        _terminalControl = new TerminalControl();
        TerminalArea.Child = _terminalControl;
        _terminalControl.Attach(session);
        _terminalControl.Focus();
    }

    private void DetachTerminal()
    {
        _activeSession = null;
        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            TerminalArea.Child = null;
            _terminalControl = null;
        }
        PlaceholderText.Visibility = Visibility.Visible;
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

    public string DisplayName => System.IO.Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));
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
