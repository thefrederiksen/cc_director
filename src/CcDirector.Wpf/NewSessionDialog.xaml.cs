using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class NewSessionDialog : Window
{
    private readonly RepositoryRegistry? _registry;
    private readonly RecentSessionStore? _recentStore;

    public string? SelectedPath { get; private set; }
    public string? SelectedCustomName { get; private set; }

    public NewSessionDialog(RepositoryRegistry? registry = null, RecentSessionStore? recentStore = null)
    {
        InitializeComponent();
        _registry = registry;
        _recentStore = recentStore;

        // Populate recent sessions
        var recents = _recentStore?.GetRecent();
        if (recents != null && recents.Count > 0)
        {
            RecentList.ItemsSource = recents;
            RecentList.Visibility = Visibility.Visible;
            RecentLabel.Visibility = Visibility.Visible;
        }
        else
        {
            RecentList.Visibility = Visibility.Collapsed;
            RecentLabel.Visibility = Visibility.Collapsed;
        }

        if (_registry != null && _registry.Repositories.Count > 0)
        {
            RepoList.ItemsSource = _registry.Repositories.ToList();
            RepoList.Visibility = Visibility.Visible;
        }
        else
        {
            RepoList.Visibility = Visibility.Collapsed;
        }
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentSession recent)
        {
            PathInput.Text = recent.RepoPath;
            SelectedCustomName = recent.CustomName;

            // Deselect repo list to avoid confusion
            RepoList.SelectedItem = null;
        }
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is RepositoryConfig repo)
        {
            PathInput.Text = repo.Path;
            SelectedCustomName = null;

            // Deselect recent list to avoid confusion
            RecentList.SelectedItem = null;
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Repository Folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathInput.Text = dialog.FolderName;
            SelectedCustomName = null;

            if (_registry != null)
            {
                _registry.TryAdd(dialog.FolderName);
                RepoList.ItemsSource = _registry.Repositories.ToList();
                RepoList.Visibility = Visibility.Visible;
            }
        }
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = PathInput.Text;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
