using System.IO;
using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace Builder;

public partial class GitCloneDialog : UserControl
{
    public string RepoUrl => UrlBox.Text.Trim();
    public string RepoName { get; private set; } = string.Empty;
    public string ParentFolder => ParentPathBox.Text.Trim();

    // 親フォルダ + リポジトリ名 = クローン先フルパス
    public string DestinationPath => Path.Combine(ParentFolder, RepoName);

    public GitCloneDialog(string lastParentFolder = "")
    {
        InitializeComponent();

        // 前回値があればそれを、なければデフォルト
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ParentPathBox.Text = string.IsNullOrEmpty(lastParentFolder)
            ? Path.Combine(home, "Projects")
            : lastParentFolder;

        Loaded += (_, _) => UrlBox.Focus();
    }

    private void PasteUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            UrlBox.Text = Clipboard.GetText().Trim();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RepoName = ExtractRepoName(UrlBox.Text.Trim());
        UpdatePreview();
    }

    private void ParentPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var parent = ParentPathBox.Text.Trim();
        if (string.IsNullOrEmpty(RepoName))
        {
            ClonePathPreview.Text = "URLを入力するとクローン先が表示されます";
            ClonePathPreview.Foreground = FindResource("ThemeMutedText") as System.Windows.Media.Brush;
        }
        else if (string.IsNullOrEmpty(parent))
        {
            ClonePathPreview.Text = "親フォルダを指定してください";
            ClonePathPreview.Foreground = FindResource("ThemeMutedText") as System.Windows.Media.Brush;
        }
        else
        {
            ClonePathPreview.Text = Path.Combine(parent, RepoName);
            ClonePathPreview.Foreground = FindResource("ThemeOutputForeground") as System.Windows.Media.Brush;
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "クローン先の親フォルダを選択"
        };

        var current = ParentPathBox.Text.Trim();
        if (Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true)
            ParentPathBox.Text = dialog.FolderName;
    }

    private void OnClone(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(RepoUrl))
        {
            ShowError("リポジトリURLを入力してください。");
            return;
        }

        if (!IsValidGitUrl(RepoUrl))
        {
            ShowError("有効なGitリポジトリURLを入力してください。");
            return;
        }

        if (string.IsNullOrWhiteSpace(RepoName))
        {
            ShowError("URLからリポジトリ名を取得できませんでした。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ParentPathBox.Text))
        {
            ShowError("親フォルダを指定してください。");
            return;
        }

        if (Directory.Exists(DestinationPath))
        {
            ShowError($"「{RepoName}」フォルダは既に存在します。別の親フォルダを指定してください。");
            return;
        }

        DialogHost.CloseDialogCommand.Execute(true, this);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    public static string ExtractRepoName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            if (url.StartsWith("git@"))
            {
                var colonIdx = url.IndexOf(':');
                if (colonIdx >= 0)
                    url = "https://github.com/" + url[(colonIdx + 1)..];
            }

            var uri = new Uri(url);
            var lastSegment = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            return Path.GetFileNameWithoutExtension(lastSegment);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsValidGitUrl(string url)
    {
        if (url.StartsWith("git@")) return url.Contains(':') && url.Contains('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == "https" || uri.Scheme == "http" || uri.Scheme == "git");
    }
}
