using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class CloneRepoDialog : Window
{
    private static readonly string LastDestFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CcDirector", "last-clone-destination.txt");

    public string? RepoUrl { get; private set; }
    public string? Destination { get; private set; }

    public CloneRepoDialog()
    {
        InitializeComponent();

        // Restore last used destination, or fall back to ~/Repos
        var lastDest = LoadLastDestination();
        if (!string.IsNullOrWhiteSpace(lastDest) && Directory.Exists(lastDest))
            DestInput.Text = lastDest;
        else
        {
            var defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Repos");
            if (Directory.Exists(defaultDir))
                DestInput.Text = defaultDir;
        }

        Loaded += (_, _) => UrlInput.Focus();
    }

    private void BtnBrowseGitHub_Click(object sender, RoutedEventArgs e)
    {
        var picker = new GitHubRepoPickerDialog { Owner = this };
        if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedUrl))
            UrlInput.Text = picker.SelectedUrl;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder"
        };

        if (dialog.ShowDialog(this) == true)
            DestInput.Text = dialog.FolderName;
    }

    private void BtnClone_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text.Trim();
        var dest = DestInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Please enter a repository URL.", "Clone Repository",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(dest))
        {
            MessageBox.Show(this, "Please select a destination folder.", "Clone Repository",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Derive repo name from URL for sub-folder
        var repoName = Path.GetFileNameWithoutExtension(url.TrimEnd('/').Split('/').LastOrDefault() ?? "repo");
        var fullDest = Path.Combine(dest, repoName);

        RepoUrl = url;
        Destination = fullDest;
        SaveLastDestination(dest);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static string? LoadLastDestination()
    {
        try { return File.Exists(LastDestFile) ? File.ReadAllText(LastDestFile).Trim() : null; }
        catch { return null; }
    }

    private static void SaveLastDestination(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(LastDestFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LastDestFile, path);
        }
        catch { /* best effort */ }
    }
}
