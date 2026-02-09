using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Git;

namespace CcDirector.Wpf.Controls;

public abstract class GitTreeNode
{
    public string DisplayName { get; set; } = "";
}

public class GitFolderNode : GitTreeNode
{
    public ObservableCollection<GitTreeNode> Children { get; } = new();
}

public class GitFileLeafNode : GitTreeNode
{
    public string FolderPath { get; set; } = "";
    public string StatusChar { get; set; } = "";
    public SolidColorBrush StatusBrush { get; set; } = null!;
}

public partial class GitChangesControl : UserControl
{
    private static readonly SolidColorBrush BrushModified = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)));
    private static readonly SolidColorBrush BrushAdded = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly SolidColorBrush BrushDeleted = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly SolidColorBrush BrushRenamed = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly SolidColorBrush BrushUntracked = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly SolidColorBrush BrushDefault = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));

    private readonly GitStatusProvider _provider = new();
    private DispatcherTimer? _pollTimer;
    private string? _repoPath;

    public GitChangesControl()
    {
        InitializeComponent();
    }

    public void Attach(string repoPath)
    {
        Detach();
        _repoPath = repoPath;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        _ = RefreshAsync();
    }

    public void Detach()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _repoPath = null;

        StagedTree.ItemsSource = null;
        ChangesTree.ItemsSource = null;
        StagedSection.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_repoPath == null) return;

        var result = await _provider.GetStatusAsync(_repoPath);
        if (!result.Success) return;

        var stagedNodes = BuildTree(result.StagedChanges);
        var unstagedNodes = BuildTree(result.UnstagedChanges);

        StagedTree.ItemsSource = stagedNodes;
        ChangesTree.ItemsSource = unstagedNodes;

        if (result.StagedChanges.Count > 0)
        {
            StagedSection.Visibility = Visibility.Visible;
            StagedBadge.Text = result.StagedChanges.Count.ToString();
        }
        else
        {
            StagedSection.Visibility = Visibility.Collapsed;
        }

        ChangesBadge.Text = result.UnstagedChanges.Count.ToString();
        EmptyText.Visibility = result.StagedChanges.Count == 0 && result.UnstagedChanges.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal static List<GitTreeNode> BuildTree(IReadOnlyList<GitFileEntry> files)
    {
        var root = new GitFolderNode();

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.FilePath)?.Replace('/', '\\') ?? "";
            var segments = string.IsNullOrEmpty(dir)
                ? Array.Empty<string>()
                : dir.Split('\\');

            var current = root;
            foreach (var segment in segments)
            {
                var existing = current.Children.OfType<GitFolderNode>()
                    .FirstOrDefault(f => f.DisplayName == segment);
                if (existing == null)
                {
                    existing = new GitFolderNode { DisplayName = segment };
                    current.Children.Add(existing);
                }
                current = existing;
            }

            current.Children.Add(new GitFileLeafNode
            {
                DisplayName = file.FileName,
                FolderPath = dir,
                StatusChar = file.Status == GitFileStatus.Untracked ? "U" : file.StatusChar,
                StatusBrush = GetStatusBrush(file.Status)
            });
        }

        CompactFolders(root);
        return [.. root.Children];
    }

    internal static void CompactFolders(GitFolderNode folder)
    {
        for (int i = 0; i < folder.Children.Count; i++)
        {
            if (folder.Children[i] is not GitFolderNode child) continue;

            // Merge single-child folder chains: src > controls → src\controls
            while (child.Children.Count == 1 && child.Children[0] is GitFolderNode grandchild)
            {
                child.DisplayName = child.DisplayName + "\\" + grandchild.DisplayName;
                var items = grandchild.Children.ToList();
                child.Children.Clear();
                foreach (var item in items)
                    child.Children.Add(item);
            }

            CompactFolders(child);
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetStatusBrush(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified => BrushModified,
        GitFileStatus.Added => BrushAdded,
        GitFileStatus.Deleted => BrushDeleted,
        GitFileStatus.Renamed => BrushRenamed,
        GitFileStatus.Copied => BrushRenamed,
        GitFileStatus.Untracked => BrushUntracked,
        _ => BrushDefault
    };
}
