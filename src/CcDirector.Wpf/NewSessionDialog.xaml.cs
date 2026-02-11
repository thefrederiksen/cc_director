using System.IO;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Wpf;

/// <summary>
/// View model for displaying Claude sessions in the Resume Session tab.
/// Wraps ClaudeSessionMetadata with display-friendly properties.
/// </summary>
public class ClaudeSessionViewModel
{
    private readonly ClaudeSessionMetadata _metadata;

    public ClaudeSessionViewModel(ClaudeSessionMetadata metadata)
    {
        _metadata = metadata;
    }

    /// <summary>The underlying metadata.</summary>
    public ClaudeSessionMetadata Metadata => _metadata;

    /// <summary>The Claude session ID for resuming.</summary>
    public string SessionId => _metadata.SessionId;

    /// <summary>Extract project name from path.</summary>
    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_metadata.ProjectPath))
                return "Unknown Project";
            return Path.GetFileName(_metadata.ProjectPath.TrimEnd('\\', '/'));
        }
    }

    /// <summary>The full project path.</summary>
    public string ProjectPath => _metadata.ProjectPath ?? string.Empty;

    /// <summary>Message count display (e.g., "42 msgs").</summary>
    public string MessageCountDisplay => $"{_metadata.MessageCount} msgs";

    /// <summary>Time ago display (e.g., "2h ago", "3d ago").</summary>
    public string TimeAgo
    {
        get
        {
            if (_metadata.Modified == DateTime.MinValue)
                return string.Empty;

            var span = DateTime.UtcNow - _metadata.Modified.ToUniversalTime();

            if (span.TotalMinutes < 1)
                return "just now";
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30)
                return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365)
                return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{(int)(span.TotalDays / 365)}y ago";
        }
    }

    /// <summary>Summary or first prompt for display.</summary>
    public string DisplaySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_metadata.Summary))
                return TruncateWithEllipsis(_metadata.Summary, 120);

            if (!string.IsNullOrWhiteSpace(_metadata.FirstPrompt))
                return TruncateWithEllipsis(_metadata.FirstPrompt, 120);

            return $"{_metadata.MessageCount} messages";
        }
    }

    private static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Remove newlines for single-line display
        text = text.Replace("\r", " ").Replace("\n", " ");

        // Collapse multiple spaces
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        if (text.Length <= maxLength)
            return text.Trim();

        return text.Substring(0, maxLength - 3).Trim() + "...";
    }
}

public partial class NewSessionDialog : Window
{
    private readonly RepositoryRegistry? _registry;
    private List<ClaudeSessionViewModel>? _allSessions;
    private List<RepositoryConfig>? _allRepos;
    private bool _sessionsLoaded;

    /// <summary>The selected path (for new session or resume).</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>The Claude session ID to resume (null for new session).</summary>
    public string? SelectedResumeSessionId { get; private set; }

    public NewSessionDialog(RepositoryRegistry? registry = null)
    {
        FileLog.Write("[NewSessionDialog] Constructor: initializing");
        InitializeComponent();
        _registry = registry;

        // Set dialog size to 80% of screen
        Width = SystemParameters.PrimaryScreenWidth * 0.8;
        Height = SystemParameters.PrimaryScreenHeight * 0.7;
        MinWidth = 900;
        MinHeight = 600;

        // Load repositories immediately (typically fast)
        if (_registry != null && _registry.Repositories.Count > 0)
        {
            _allRepos = _registry.Repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RepoList.ItemsSource = _allRepos;
            FileLog.Write($"[NewSessionDialog] Loaded {_allRepos.Count} repositories");
        }
        else
        {
            _allRepos = new List<RepositoryConfig>();
        }

        // Load Claude sessions async after dialog is shown
        Loaded += async (_, _) =>
        {
            SessionSearchBox.Focus();
            await LoadClaudeSessionsAsync();
        };

        FileLog.Write("[NewSessionDialog] Constructor: complete");
    }

    private async Task LoadClaudeSessionsAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadClaudeSessionsAsync: starting");

        try
        {
            var sessions = await Task.Run(() => ClaudeSessionReader.ScanAllProjects());
            FileLog.Write($"[NewSessionDialog] LoadClaudeSessionsAsync: found {sessions.Count} sessions");

            _allSessions = sessions.Select(s => new ClaudeSessionViewModel(s)).ToList();
            _sessionsLoaded = true;

            // Update UI
            LoadingText.Visibility = Visibility.Collapsed;

            if (_allSessions.Count > 0)
            {
                SessionList.ItemsSource = _allSessions;
                SessionList.Visibility = Visibility.Visible;
            }
            else
            {
                NoSessionsText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadClaudeSessionsAsync FAILED: {ex.Message}");
            LoadingText.Visibility = Visibility.Collapsed;
            NoSessionsText.Text = "Error loading sessions";
            NoSessionsText.Visibility = Visibility.Visible;
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs)
            return;

        UpdateActionButton();
    }

    private void UpdateActionButton()
    {
        if (MainTabs.SelectedIndex == 0) // Resume Session tab
        {
            BtnAction.Content = "Resume Selected";
            BtnAction.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#22C55E")!);
            BtnAction.IsEnabled = SessionList.SelectedItem != null;
        }
        else // New Session tab
        {
            BtnAction.Content = "Start Session";
            BtnAction.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007ACC")!);
            BtnAction.IsEnabled = !string.IsNullOrWhiteSpace(PathInput.Text);
        }
    }

    private void SessionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_sessionsLoaded || _allSessions == null)
            return;

        var filter = SessionSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            SessionList.ItemsSource = _allSessions;
        }
        else
        {
            SessionList.ItemsSource = _allSessions
                .Where(s => s.ProjectName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.DisplaySummary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void RepoSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allRepos == null)
            return;

        var filter = RepoSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            RepoList.ItemsSource = _allRepos;
        }
        else
        {
            RepoList.ItemsSource = _allRepos
                .Where(r => (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (r.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is ClaudeSessionViewModel vm)
        {
            SelectedResumeSessionId = vm.SessionId;
            SelectedPath = vm.ProjectPath;
            FileLog.Write($"[NewSessionDialog] Session selected: {vm.SessionId}, path: {vm.ProjectPath}");
        }
        else
        {
            SelectedResumeSessionId = null;
        }

        UpdateActionButton();
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is RepositoryConfig repo)
        {
            PathInput.Text = repo.Path;
            SelectedPath = repo.Path;
            SelectedResumeSessionId = null;
            FileLog.Write($"[NewSessionDialog] Repo selected: {repo.Path}");
        }

        UpdateActionButton();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnBrowse_Click");

        var dialog = new OpenFolderDialog
        {
            Title = "Select Repository Folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathInput.Text = dialog.FolderName;
            SelectedPath = dialog.FolderName;
            SelectedResumeSessionId = null;

            // Clear repo selection
            RepoList.SelectedItem = null;

            // Add to registry if not already there
            if (_registry != null)
            {
                _registry.TryAdd(dialog.FolderName);
                _allRepos = _registry.Repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
                RepoList.ItemsSource = _allRepos;
            }

            UpdateActionButton();
            FileLog.Write($"[NewSessionDialog] Browsed to: {dialog.FolderName}");
        }
    }

    private void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 0) // Resume Session tab
        {
            if (string.IsNullOrEmpty(SelectedResumeSessionId))
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No session selected for resume");
                return;
            }

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Resuming session {SelectedResumeSessionId}");
            DialogResult = true;
        }
        else // New Session tab
        {
            SelectedPath = PathInput.Text;
            SelectedResumeSessionId = null; // Ensure we're starting a new session

            if (string.IsNullOrWhiteSpace(SelectedPath))
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No path specified for new session");
                return;
            }

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting new session at {SelectedPath}");
            DialogResult = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnCancel_Click");
        DialogResult = false;
    }
}
