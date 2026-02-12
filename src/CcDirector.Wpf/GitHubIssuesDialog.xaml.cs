using System.Diagnostics;
using System.Windows;

namespace CcDirector.Wpf;

public partial class GitHubIssuesDialog : Window
{
    private readonly string _url;

    public GitHubIssuesDialog(string url)
    {
        InitializeComponent();
        _url = url;
        UrlTextBox.Text = url;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_url);
        Close();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        Close();
    }
}
