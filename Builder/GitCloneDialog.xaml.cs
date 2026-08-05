using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Builder.Services;
using MaterialDesignThemes.Wpf;

namespace Builder;

/// <summary>一覧モードで1リポジトリを表す行。</summary>
public class GitHubRepoItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public GitHubRepoItem(GitHubRepository repository)
    {
        Repository = repository;
    }

    public GitHubRepository Repository { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Notify();
            SelectionChanged?.Invoke();
        }
    }

    private bool _canSelect = true;
    public bool CanSelect
    {
        get => _canSelect;
        set
        {
            if (_canSelect == value) return;
            _canSelect = value;
            Notify();
        }
    }

    /// <summary>「登録済み」「フォルダ有り」などクローンできない理由。空なら選択可能。</summary>
    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            Notify();
        }
    }

    /// <summary>並べ替え用（CollectionView から入れ子プロパティを参照しないための別名）。</summary>
    public DateTimeOffset UpdatedAt => Repository.UpdatedAt;

    public string MetaText => string.IsNullOrEmpty(Repository.Language)
        ? Repository.UpdatedAt.LocalDateTime.ToString("yyyy/MM/dd")
        : $"{Repository.Language} · {Repository.UpdatedAt.LocalDateTime:yyyy/MM/dd}";

    public Action? SelectionChanged { get; set; }
}

public partial class GitCloneDialog : UserControl
{
    private readonly GitHubService _gitHubService = new();
    private readonly HashSet<string> _existingProjectPaths;
    private CancellationTokenSource? _fetchCts;

    /// <summary>一覧取得の対象になり得るユーザー/組織名。単体URLのときは空。</summary>
    private string _detectedOwner = string.Empty;

    /// <summary>一覧を取得済みの所有者。</summary>
    private string _loadedOwner = string.Empty;

    private readonly ObservableCollection<GitHubRepoItem> _repoItems = [];
    private ICollectionView? _repoView;

    public string RepoUrl => UrlBox.Text.Trim();
    public string RepoName { get; private set; } = string.Empty;
    public string ParentFolder => ParentPathBox.Text.Trim();

    // 親フォルダ + リポジトリ名 = クローン先フルパス
    public string DestinationPath => Path.Combine(ParentFolder, RepoName);

    /// <summary>一覧モードでチェックされたリポジトリ。単体クローンのときは空。</summary>
    public IReadOnlyList<GitHubRepository> SelectedRepositories { get; private set; } = [];

    public GitCloneDialog(string lastParentFolder = "", IEnumerable<string>? existingProjectPaths = null)
    {
        InitializeComponent();

        _existingProjectPaths = new HashSet<string>(
            (existingProjectPaths ?? []).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        // 前回値があればそれを、なければデフォルト
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ParentPathBox.Text = string.IsNullOrEmpty(lastParentFolder)
            ? Path.Combine(home, "Projects")
            : lastParentFolder;

        Loaded += (_, _) => UrlBox.Focus();
    }

    private bool IsBulkMode => RepoListSection.Visibility == Visibility.Visible;

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
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
        var text = UrlBox.Text.Trim();
        RepoName = ExtractRepoName(text);

        _detectedOwner = GitHubService.TryParseOwner(text, out var owner) ? owner : string.Empty;
        FetchReposButton.IsEnabled = _detectedOwner.Length > 0;

        // 入力が別の所有者/単体URLに変わったら一覧を隠す
        if (IsBulkMode && !_detectedOwner.Equals(_loadedOwner, StringComparison.OrdinalIgnoreCase))
            HideRepoList();

        UpdatePreview();
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnClone(sender, e);
    }

    private void ParentPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshRepoAvailability();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var parent = ParentPathBox.Text.Trim();
        var muted = FindResource("ThemeMutedText") as System.Windows.Media.Brush;
        var normal = FindResource("ThemeOutputForeground") as System.Windows.Media.Brush;

        if (IsBulkMode)
        {
            var count = _repoItems.Count(i => i.IsSelected);
            CloneButtonText.Text = count > 0 ? $"{count}件をクローン" : "クローン";
            PrimaryButtonIcon.Kind = PackIconKind.SourceBranchSync;

            if (string.IsNullOrEmpty(parent))
            {
                ClonePathPreview.Text = "親フォルダを指定してください";
                ClonePathPreview.Foreground = muted;
            }
            else if (count == 0)
            {
                ClonePathPreview.Text = $"{parent} （取り込むリポジトリを選択してください）";
                ClonePathPreview.Foreground = muted;
            }
            else
            {
                ClonePathPreview.Text = $"{parent} に {count} 件をクローン";
                ClonePathPreview.Foreground = normal;
            }

            UpdateRepoSummary();
            return;
        }

        // ユーザー/組織のURLならボタンを押した時点で一覧取得に進む
        var isOwnerInput = _detectedOwner.Length > 0;
        CloneButtonText.Text = isOwnerInput ? "リポジトリ一覧" : "クローン";
        PrimaryButtonIcon.Kind = isOwnerInput ? PackIconKind.FormatListChecks : PackIconKind.SourceBranchSync;

        if (string.IsNullOrEmpty(RepoName))
        {
            ClonePathPreview.Text = isOwnerInput
                ? $"{_detectedOwner} のリポジトリ一覧から選んで一括クローンします"
                : "URLを入力するとクローン先が表示されます";
            ClonePathPreview.Foreground = muted;
        }
        else if (string.IsNullOrEmpty(parent))
        {
            ClonePathPreview.Text = "親フォルダを指定してください";
            ClonePathPreview.Foreground = muted;
        }
        else
        {
            ClonePathPreview.Text = Path.Combine(parent, RepoName);
            ClonePathPreview.Foreground = normal;
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

    // ---- 一覧モード ----

    private async void FetchRepos_Click(object sender, RoutedEventArgs e)
        => await FetchRepositoriesAsync();

    private async Task FetchRepositoriesAsync()
    {
        var owner = _detectedOwner;
        if (owner.Length == 0) return;

        ErrorText.Visibility = Visibility.Collapsed;
        LoadingBar.Visibility = Visibility.Visible;
        FetchReposButton.IsEnabled = false;
        PrimaryButton.IsEnabled = false;
        ClonePathPreview.Text = $"{owner} のリポジトリ一覧を取得しています...";

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();

        try
        {
            var repos = await _gitHubService.GetRepositoriesAsync(owner, _fetchCts.Token);
            if (repos.Count == 0)
            {
                ShowError($"「{owner}」に取得できるリポジトリがありません。");
                return;
            }

            ShowRepoList(owner, repos);
        }
        catch (OperationCanceledException)
        {
            // 別の取得に置き換えられただけなので何もしない
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            FetchReposButton.IsEnabled = _detectedOwner.Length > 0;
            PrimaryButton.IsEnabled = true;
            UpdatePreview();
        }
    }

    private void ShowRepoList(string owner, IReadOnlyList<GitHubRepository> repos)
    {
        _loadedOwner = owner;
        _repoItems.Clear();

        foreach (var repo in repos)
        {
            var item = new GitHubRepoItem(repo);
            item.SelectionChanged = UpdatePreview;
            _repoItems.Add(item);
        }

        if (_repoView == null)
        {
            _repoView = CollectionViewSource.GetDefaultView(_repoItems);
            _repoView.Filter = RepoFilter;
            // まだ取り込んでいないリポジトリを先頭に、その中では更新日の新しい順
            _repoView.SortDescriptions.Add(new SortDescription(nameof(GitHubRepoItem.CanSelect), ListSortDirection.Descending));
            _repoView.SortDescriptions.Add(new SortDescription(nameof(GitHubRepoItem.UpdatedAt), ListSortDirection.Descending));
            RepoList.ItemsSource = _repoView;
        }

        RepoFilterBox.Text = string.Empty;
        RepoListSection.Visibility = Visibility.Visible;
        RootCard.Width = 620;

        RefreshRepoAvailability();
        UpdatePreview();
    }

    private void HideRepoList()
    {
        RepoListSection.Visibility = Visibility.Collapsed;
        RootCard.Width = 500;
        _loadedOwner = string.Empty;
        _repoItems.Clear();
    }

    /// <summary>親フォルダの内容に応じて、各リポジトリがクローン可能かを再判定します。</summary>
    private void RefreshRepoAvailability()
    {
        if (_repoItems.Count == 0) return;

        var parent = ParentPathBox.Text.Trim();
        foreach (var item in _repoItems)
        {
            if (string.IsNullOrEmpty(parent))
            {
                item.CanSelect = true;
                item.StatusText = string.Empty;
                continue;
            }

            string dest;
            try
            {
                dest = Path.Combine(parent, item.Repository.Name);
            }
            catch
            {
                item.CanSelect = true;
                item.StatusText = string.Empty;
                continue;
            }

            if (_existingProjectPaths.Contains(NormalizePath(dest)))
            {
                item.IsSelected = false;
                item.CanSelect = false;
                item.StatusText = "登録済み";
            }
            else if (Directory.Exists(dest))
            {
                item.IsSelected = false;
                item.CanSelect = false;
                item.StatusText = "フォルダ有り";
            }
            else
            {
                item.CanSelect = true;
                item.StatusText = string.Empty;
            }
        }

        // CanSelect が変わると並び順も変わるため作り直す
        _repoView?.Refresh();
    }

    private void UpdateRepoSummary()
    {
        var selectable = _repoItems.Count(i => i.CanSelect);
        var selected = _repoItems.Count(i => i.IsSelected);
        RepoSummaryText.Text = $"{_loadedOwner} のリポジトリ {_repoItems.Count} 件（選択可 {selectable} 件） / 選択中 {selected} 件";
    }

    private bool RepoFilter(object obj)
    {
        if (obj is not GitHubRepoItem item) return false;
        var keyword = RepoFilterBox.Text.Trim();
        if (keyword.Length == 0) return true;

        return item.Repository.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || (item.Repository.Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
               || (item.Repository.Language?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RepoFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        => _repoView?.Refresh();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // 絞り込み中は表示されている行だけを対象にする
        foreach (var item in VisibleItems().Where(i => i.CanSelect))
            item.IsSelected = true;
        UpdatePreview();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in VisibleItems())
            item.IsSelected = false;
        UpdatePreview();
    }

    private IEnumerable<GitHubRepoItem> VisibleItems()
        => _repoView?.Cast<GitHubRepoItem>().ToList() ?? _repoItems.ToList();

    // ---- 確定 ----

    private void OnClone(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        // ユーザー/組織のURLが入力されている場合は、クローンではなく一覧取得を行う
        if (_detectedOwner.Length > 0 && !IsBulkMode)
        {
            _ = FetchRepositoriesAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(ParentPathBox.Text))
        {
            ShowError("親フォルダを指定してください。");
            return;
        }

        if (IsBulkMode)
        {
            var selected = _repoItems.Where(i => i.IsSelected).Select(i => i.Repository).ToList();
            if (selected.Count == 0)
            {
                ShowError("取り込むリポジトリをチェックしてください。");
                return;
            }

            SelectedRepositories = selected;
            _fetchCts?.Cancel();
            DialogHost.CloseDialogCommand.Execute(true, this);
            return;
        }

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
        if (GitHubService.TryParseOwner(url, out _)) return string.Empty;

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
