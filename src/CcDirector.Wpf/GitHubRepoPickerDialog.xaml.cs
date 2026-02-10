using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CcDirector.Wpf;

public partial class GitHubRepoPickerDialog : Window
{
    private List<RepoItem> _allRepos = [];

    public string? SelectedUrl { get; private set; }

    public GitHubRepoPickerDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadReposAsync();
        RepoList.SelectionChanged += (_, _) =>
            BtnSelect.IsEnabled = RepoList.SelectedItem is not null;
    }

    private async Task LoadReposAsync()
    {
        StatusText.Text = "Loading repositories...";
        FilterBox.IsEnabled = false;

        try
        {
            var json = await RunGhAsync(
                "repo list --limit 100 --json name,url,description,isPrivate,updatedAt");

            if (json is null)
                return; // error already shown

            var repos = JsonSerializer.Deserialize<List<GhRepo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (repos is null || repos.Count == 0)
            {
                StatusText.Text = "No repositories found.";
                return;
            }

            _allRepos = repos
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new RepoItem(r))
                .ToList();
            RepoList.ItemsSource = _allRepos;
            StatusText.Text = $"{_allRepos.Count} repositories";
            FilterBox.IsEnabled = true;
            FilterBox.Focus();
        }
        catch (JsonException)
        {
            StatusText.Text = "Failed to parse response from gh CLI.";
        }
    }

    private async Task<string?> RunGhAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                StatusText.Text = "Failed to start gh CLI.";
                return null;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                StatusText.Text = error.Contains("auth login")
                    ? "gh CLI is not authenticated. Run 'gh auth login' first."
                    : $"gh error: {error.Trim().Split('\n').FirstOrDefault()}";
                return null;
            }

            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "gh CLI not found. Install it from https://cli.github.com";
            return null;
        }
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            RepoList.ItemsSource = _allRepos;
            StatusText.Text = $"{_allRepos.Count} repositories";
        }
        else
        {
            var filtered = _allRepos
                .Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || (r.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            RepoList.ItemsSource = filtered;
            StatusText.Text = $"{filtered.Count} of {_allRepos.Count} repositories";
        }
    }

    private void RepoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RepoList.SelectedItem is RepoItem repo)
            AcceptSelection(repo);
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        if (RepoList.SelectedItem is RepoItem repo)
            AcceptSelection(repo);
    }

    private void AcceptSelection(RepoItem repo)
    {
        SelectedUrl = repo.Url;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // --- Data models ---

    internal sealed class GhRepo
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Description { get; set; }
        public bool IsPrivate { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class RepoItem
    {
        private static readonly SolidColorBrush PrivateBg = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x40, 0x00)));
        private static readonly SolidColorBrush PrivateFg = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x3D)));
        private static readonly SolidColorBrush PublicBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x4B, 0x2A)));
        private static readonly SolidColorBrush PublicFg = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)));

        public string Name { get; }
        public string Url { get; }
        public string? Description { get; }
        public string BadgeText { get; }
        public SolidColorBrush BadgeBackground { get; }
        public SolidColorBrush BadgeForeground { get; }
        public Visibility DescriptionVisibility { get; }

        public RepoItem(GhRepo repo)
        {
            Name = repo.Name;
            Url = repo.Url;
            Description = repo.Description;
            BadgeText = repo.IsPrivate ? "private" : "public";
            BadgeBackground = repo.IsPrivate ? PrivateBg : PublicBg;
            BadgeForeground = repo.IsPrivate ? PrivateFg : PublicFg;
            DescriptionVisibility = string.IsNullOrWhiteSpace(repo.Description)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
