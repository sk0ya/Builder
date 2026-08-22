using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Builder.Models;
using Builder.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;

namespace Builder.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly ProcessService _processService = new();
    private readonly RunningProcessDetector _runningProcessDetector = new();
    private AppSettings _appSettings = null!;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _gitSyncTimer;
    private readonly DispatcherTimer _runningScanTimer;
    private readonly SemaphoreSlim _gitSyncLock = new(1, 1);
    private readonly SemaphoreSlim _runningScanLock = new(1, 1);

    /// <summary>新しいログ行が追加されたときに発火（行テキストのみ、改行なし）</summary>
    public event Action<string>? LineAppended;

    /// <summary>ログがリセットされたときに発火（プロジェクト切替・クリア時）。引数は新しい全文。</summary>
    public event Action<string>? LogReset;

    public ObservableCollection<ProjectEntry> Projects { get; } = [];
    public ObservableCollection<string> GroupTabs { get; } = [];

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _highlightText = string.Empty;

    [ObservableProperty]
    private string _selectedGroupFilter = string.Empty;

    public ICollectionView FilteredProjects { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GitFetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(GitPullCommand))]
    [NotifyCanExecuteChangedFor(nameof(GitPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenGithubPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyRepoPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebuildAndRestartSelfCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebuildAndRestartCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedProject))]
    private ProjectEntry? _selectedProject;

    [ObservableProperty]
    private string _outputLog = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentBranch))]
    private string _currentBranch = string.Empty;

    public bool HasCurrentBranch => !string.IsNullOrEmpty(CurrentBranch);

    [ObservableProperty]
    private bool _isGitDirty;

    private string _backgroundColorHex = "#1E1E1E";
    private string _accentColorHex = "#4FC3F7";

    public bool HasSelectedProject => SelectedProject != null;

    public ObservableCollection<string> Branches { get; } = [];

    public MainViewModel()
    {
        FilteredProjects = CollectionViewSource.GetDefaultView(Projects);
        FilteredProjects.Filter = ProjectFilter;
        FilteredProjects.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProjectEntry.Group)));

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            HighlightText = FilterText;
            FilteredProjects.Refresh();
        };

        _appSettings = _settingsService.Load();
        LoadProjects();
        Projects.CollectionChanged += (_, _) => RefreshGroupTabs();
        RefreshGroupTabs();
        LoadTheme();

        _gitSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _gitSyncTimer.Tick += async (_, _) => await RefreshAllGitSyncStatusAsync();
        _gitSyncTimer.Start();
        _ = RefreshAllGitSyncStatusAsync();

        _runningScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _runningScanTimer.Tick += async (_, _) => await RefreshRunningStatusAsync();
        _runningScanTimer.Start();
        _ = RefreshRunningStatusAsync();
    }

    /// <summary>
    /// 全プロジェクトのフォルダを対象にWMIでプロセスをスキャンし、Builder経由でなく
    /// 手動やVS等から起動されたプロセスも実行中インジケーターに反映する。
    /// </summary>
    private async Task RefreshRunningStatusAsync()
    {
        if (!await _runningScanLock.WaitAsync(0)) return;
        try
        {
            var projects = Projects.ToList();
            var folders = projects.Select(p => p.FolderPath).ToList();

            var states = await Task.Run(() => _runningProcessDetector.DetectRunningProjects(folders));

            foreach (var project in projects)
            {
                var state = states.GetValueOrDefault(project.FolderPath, ProjectRunState.NotRunning);
                project.IsDetectedExternally = state.IsRunning;

                // Builderから起動した実行中プロセスは起動時点で権限レベルを確実に把握しているため、
                // 外部スキャンの推定値で上書きしない。
                if (!project.IsLaunchedByBuilder)
                    project.IsRunningElevated = state.IsElevated ?? false;
            }
        }
        finally
        {
            _runningScanLock.Release();
        }
    }

    private bool ProjectFilter(object item)
    {
        if (item is not ProjectEntry project) return false;
        if (project == SelectedProject) return true;
        if (!string.IsNullOrEmpty(SelectedGroupFilter) && project.Group != SelectedGroupFilter)
            return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        return project.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               project.FolderPath.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedGroupFilterChanged(string value)
    {
        FilteredProjects.Refresh();
    }

    private void RefreshGroupTabs()
    {
        var current = SelectedGroupFilter;
        GroupTabs.Clear();
        GroupTabs.Add(string.Empty);
        foreach (var g in ExistingGroups)
            GroupTabs.Add(g);

        var next = GroupTabs.Contains(current) ? current : string.Empty;
        if (SelectedGroupFilter == next)
            FilteredProjects.Refresh();
        else
            SelectedGroupFilter = next;
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterTimer.Stop();
        if (string.IsNullOrEmpty(value))
        {
            HighlightText = string.Empty;
            FilteredProjects.Refresh(); // クリア時は即時反映
        }
        else
            _filterTimer.Start();
    }

    private void LoadTheme()
    {
        _backgroundColorHex = _appSettings.BackgroundColor;
        _accentColorHex = _appSettings.AccentColor;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var bgColor = ParseColor(_backgroundColorHex);
        var accentColor = ParseColor(_accentColorHex);
        var isLight = IsLightColor(bgColor);

        // Switch MaterialDesign base theme (Light/Dark) so all internal templates update
        var paletteHelper = new PaletteHelper();
        var theme = paletteHelper.GetTheme();
        theme.SetBaseTheme(isLight ? BaseTheme.Light : BaseTheme.Dark);
        paletteHelper.SetTheme(theme);

        var res = Application.Current.Resources;
        res["MaterialDesignPaper"] = new SolidColorBrush(bgColor);
        res["MaterialDesignCardBackground"] = new SolidColorBrush(AdjustBrightness(bgColor, isLight ? -10 : 10));
        res["MaterialDesignToolBarBackground"] = new SolidColorBrush(AdjustBrightness(bgColor, isLight ? -8 : -5));
        res["MaterialDesignBody"] = new SolidColorBrush(isLight ? Color.FromRgb(0x21, 0x21, 0x21) : Color.FromRgb(0xDD, 0xDD, 0xDD));
        res["PrimaryHueMidBrush"] = new SolidColorBrush(accentColor);
        res["PrimaryHueMidForegroundBrush"] = new SolidColorBrush(Colors.White);
        res["SecondaryHueMidBrush"] = new SolidColorBrush(accentColor);
        res["ThemeTitleBar"] = new SolidColorBrush(AdjustBrightness(bgColor, isLight ? -8 : -5));
        res["ThemeSidebar"] = new SolidColorBrush(AdjustBrightness(bgColor, isLight ? -10 : 10));
        res["ThemeOutput"] = new SolidColorBrush(AdjustBrightness(bgColor, isLight ? -8 : -5));
        res["ThemeAccent"] = new SolidColorBrush(accentColor);
        res["ThemeSubText"] = new SolidColorBrush(isLight ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x99, 0x99, 0x99));
        res["ThemeMutedText"] = new SolidColorBrush(isLight ? Color.FromRgb(0x88, 0x88, 0x88) : Color.FromRgb(0x66, 0x66, 0x66));
        res["ThemeOutputForeground"] = new SolidColorBrush(isLight ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xCC, 0xCC, 0xCC));
        res["ThemeBorder"] = new SolidColorBrush(isLight ? Color.FromRgb(0xCC, 0xCC, 0xCC) : Color.FromRgb(0x33, 0x33, 0x33));
        res["ThemeSplitter"] = new SolidColorBrush(isLight ? Color.FromRgb(0xDD, 0xDD, 0xDD) : Color.FromRgb(0x33, 0x33, 0x33));
    }

    private static bool IsLightColor(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance > 0.5;
    }

    private static Color AdjustBrightness(Color color, int amount)
    {
        return Color.FromRgb(
            (byte)Math.Clamp(color.R + amount, 0, 255),
            (byte)Math.Clamp(color.G + amount, 0, 255),
            (byte)Math.Clamp(color.B + amount, 0, 255));
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Black;
        }
    }

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _appSettings.Projects)
            Projects.Add(p);
    }

    private void SaveProjects()
    {
        _appSettings.Projects = [.. Projects];
        _appSettings.BackgroundColor = _backgroundColorHex;
        _appSettings.AccentColor = _accentColorHex;
        _settingsService.Save(_appSettings);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var dialog = new SettingsDialog
        {
            Owner = Application.Current.MainWindow,
            BackgroundColorHex = _backgroundColorHex,
            AccentColorHex = _accentColorHex,
            OnThemeChanged = (bgHex, accentHex) =>
            {
                _backgroundColorHex = bgHex;
                _accentColorHex = accentHex;
                ApplyTheme();
            }
        };

        dialog.Closed += (_, _) =>
        {
            _backgroundColorHex = dialog.BackgroundColorHex;
            _accentColorHex = dialog.AccentColorHex;
            SaveProjects();
        };
        dialog.Show();
    }

    [RelayCommand]
    private void AddProject()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "プロジェクトフォルダを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            var folderPath = dialog.FolderName;
            var entry = new ProjectEntry
            {
                Name = Path.GetFileName(folderPath) ?? folderPath,
                FolderPath = folderPath
            };
            ProjectDetector.DetectAndApply(entry);
            Projects.Add(entry);
            SelectedProject = entry;
            SaveProjects();
        }
    }

    [RelayCommand]
    private async Task AddProjectFromGithub()
    {
        var view = new GitCloneDialog(_appSettings.LastCloneParentFolder,
                                      Projects.Select(p => p.FolderPath));
        var result = await DialogHost.Show(view, "RootDialog");
        if (result is not true) return;

        // 親フォルダを保存
        _appSettings.LastCloneParentFolder = view.ParentFolder;
        _settingsService.Save(_appSettings);

        // 一覧から複数選択された場合は一括クローン
        if (view.SelectedRepositories.Count > 0)
        {
            await RunBulkGitCloneAsync(view.SelectedRepositories, view.ParentFolder);
            return;
        }

        var url = view.RepoUrl;
        var destPath = view.DestinationPath;
        var projectName = Path.GetFileName(destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(projectName))
            projectName = GitCloneDialog.ExtractRepoName(url);

        var parentDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            try { Directory.CreateDirectory(parentDir); }
            catch (Exception ex)
            {
                AppendLog($"[エラー] フォルダの作成に失敗しました: {ex.Message}");
                return;
            }
        }

        await RunGitCloneAsync(url, destPath, string.IsNullOrEmpty(projectName) ? "Repository" : projectName);
    }

    /// <summary>
    /// GitHubの一覧でチェックされたリポジトリを、親フォルダ配下へ順番にクローンして
    /// プロジェクトに追加します。1件失敗しても残りは続行します。
    /// </summary>
    private async Task RunBulkGitCloneAsync(IReadOnlyList<GitHubRepository> repositories, string parentFolder)
    {
        try { Directory.CreateDirectory(parentFolder); }
        catch (Exception ex)
        {
            AppendLog($"[エラー] フォルダの作成に失敗しました: {ex.Message}");
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();

        // 途中で選択を切り替えると進捗ログが別タブに移ってしまうため、
        // 一括処理中は開始時の選択プロジェクトにログを出し続ける。
        var logOwner = SelectedProject;
        AppendLog($"[一括クローン] {repositories.Count} 件のリポジトリを取り込みます。", logOwner);

        var succeeded = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < repositories.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var repo = repositories[i];
                var destPath = Path.Combine(parentFolder, repo.Name);
                AppendLog($"[一括クローン] ({i + 1}/{repositories.Count}) {repo.FullName}", logOwner);

                if (Directory.Exists(destPath))
                {
                    AppendLog($"[スキップ] 「{repo.Name}」フォルダは既に存在します。", logOwner);
                    failed++;
                    continue;
                }

                if (await CloneRepositoryAsync(repo.CloneUrl, destPath, repo.Name, logOwner, false, _cts.Token))
                    succeeded++;
                else
                    failed++;
            }

            var cancelled = _cts.Token.IsCancellationRequested;
            AppendLog(cancelled
                ? $"[一括クローン] キャンセルしました。（成功 {succeeded} 件 / 失敗・スキップ {failed} 件）"
                : $"[一括クローン] 完了しました。（成功 {succeeded} 件 / 失敗・スキップ {failed} 件）", logOwner);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }

        if (succeeded > 0) _ = RefreshAllGitSyncStatusAsync();
    }

    private async Task RunGitCloneAsync(string url, string destPath, string projectName)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            await CloneRepositoryAsync(url, destPath, projectName, SelectedProject, true, _cts.Token);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    /// <summary>
    /// git clone を1件実行し、成功したらプロジェクトとして追加します。
    /// IsBusy/_cts の管理は呼び出し側が行います。
    /// </summary>
    /// <param name="logOwner">ログの出力先プロジェクト（null なら共通ログ）。</param>
    /// <param name="selectNewProject">クローンしたプロジェクトを選択状態にするか。</param>
    private async Task<bool> CloneRepositoryAsync(
        string url, string destPath, string projectName,
        ProjectEntry? logOwner, bool selectNewProject, CancellationToken ct)
    {
        var project = logOwner;
        AppendCommandLog($"> git clone {url}", project);
        AppendCommandLog($"  → {destPath}", project);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(destPath) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("clone");
            psi.ArgumentList.Add("--progress");
            psi.ArgumentList.Add(url);
            psi.ArgumentList.Add(destPath);

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Application.Current.Dispatcher.Invoke(() => AppendLog(e.Data, project));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Application.Current.Dispatcher.Invoke(() => AppendLog(e.Data, project));
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);
            process.WaitForExit();

            if (process.ExitCode == 0 && Directory.Exists(destPath))
            {
                var entry = new ProjectEntry
                {
                    Name = projectName,
                    FolderPath = destPath
                };
                ProjectDetector.DetectAndApply(entry);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Projects.Add(entry);
                    if (selectNewProject) SelectedProject = entry;
                    SaveProjects();
                });
                AppendLog($"[完了] プロジェクト「{projectName}」を追加しました。",
                          selectNewProject ? entry : project);
                return true;
            }

            if (process.ExitCode != 0)
                AppendLog($"[エラー] git clone が失敗しました (終了コード: {process.ExitCode})", project);

            return false;
        }
        catch (OperationCanceledException)
        {
            AppendLog("[キャンセル]", project);
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}", project);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private void RemoveProject()
    {
        if (SelectedProject == null) return;
        Projects.Remove(SelectedProject);
        SelectedProject = Projects.FirstOrDefault();
        SaveProjects();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task GitFetch()
    {
        if (SelectedProject == null) return;

        if (!SelectedProject.IsGitRepository)
        {
            AppendLog("[エラー] このフォルダはGitリポジトリではありません。");
            return;
        }

        await RunCommandAsync(SelectedProject.FolderPath, "git fetch");
        RefreshCurrentBranch();
        _ = RefreshGitDirtyStatusAsync();
        _ = RefreshAllGitSyncStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task GitPull()
    {
        if (SelectedProject == null) return;

        if (!SelectedProject.IsGitRepository)
        {
            AppendLog("[エラー] このフォルダはGitリポジトリではありません。");
            return;
        }

        await RunCommandAsync(SelectedProject.FolderPath, "git pull");
        RefreshCurrentBranch();
        _ = RefreshGitDirtyStatusAsync();
        _ = RefreshAllGitSyncStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task GitPush()
    {
        if (SelectedProject == null) return;

        if (!SelectedProject.IsGitRepository)
        {
            AppendLog("[エラー] このフォルダはGitリポジトリではありません。");
            return;
        }

        await RunCommandAsync(SelectedProject.FolderPath, "git push");
        _ = RefreshGitDirtyStatusAsync();
        _ = RefreshAllGitSyncStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task CopyRepoPath()
    {
        if (!SelectedProject!.IsGitRepository)
        {
            AppendLog("[エラー] このフォルダはGitリポジトリではありません。");
            return;
        }

        var remoteUrl = await GetGitOriginUrlAsync(SelectedProject.FolderPath);
        if (!TryConvertToRepositoryPageUrl(remoteUrl, out var repositoryPageUrl))
        {
            AppendLog("[エラー] origin URLからGitHub/Azure DevOpsのページを特定できませんでした。");
            return;
        }

        Clipboard.SetText(repositoryPageUrl);
        AppendLog($"[コピー] {repositoryPageUrl}");
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task OpenGithubPage()
    {
        if (SelectedProject == null) return;

        if (!SelectedProject.IsGitRepository)
        {
            AppendLog("[エラー] このフォルダはGitリポジトリではありません。");
            return;
        }

        var remoteUrl = await GetGitOriginUrlAsync(SelectedProject.FolderPath);
        if (!TryConvertToRepositoryPageUrl(remoteUrl, out var repositoryPageUrl))
        {
            AppendLog("[エラー] origin URLからGitHub/Azure DevOpsのページを特定できませんでした。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = repositoryPageUrl,
                UseShellExecute = true
            });
            AppendLog($"[Repo] {repositoryPageUrl}");
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] リポジトリページを開けませんでした: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SwitchBranch(string branchName)
    {
        if (SelectedProject == null || string.IsNullOrEmpty(branchName)) return;
        if (branchName == CurrentBranch) return;

        await RunCommandAsync(SelectedProject.FolderPath, $"git switch {branchName}");
        RefreshCurrentBranch();
        await RefreshBranchesAsync();
        _ = RefreshGitDirtyStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task Build()
    {
        if (SelectedProject == null) return;

        if (string.IsNullOrWhiteSpace(SelectedProject.BuildCommand))
        {
            AppendLog("[エラー] ビルドコマンドが設定されていません。");
            return;
        }

        await RunCommandAsync(SelectedProject.FolderPath, SelectedProject.BuildCommand);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private void Launch()
    {
        if (SelectedProject == null) return;
        LaunchProject(SelectedProject);
    }

    [RelayCommand]
    private void LaunchAsAdmin()
    {
        if (SelectedProject == null) return;
        LaunchProjectAsAdmin(SelectedProject);
    }

    private void LaunchProject(ProjectEntry project)
    {
        if (string.IsNullOrWhiteSpace(project.LaunchCommand))
        {
            AppendLog("[エラー] 起動コマンドが設定されていません。", project);
            return;
        }

        try
        {
            project.IsRunningElevated = false;
            _processService.LaunchDetached(project.FolderPath, project.LaunchCommand,
                line => Application.Current.Dispatcher.Invoke(() => AppendLog(line, project)),
                running => Application.Current.Dispatcher.Invoke(() => project.IsLaunchedByBuilder = running),
                () => Application.Current.Dispatcher.Invoke(() => project.IsRunningElevated = true));
            AppendCommandLog($"> {project.LaunchCommand}", project);
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}", project);
        }
    }

    private void LaunchProjectAsAdmin(ProjectEntry project)
    {
        if (string.IsNullOrWhiteSpace(project.LaunchCommand))
        {
            AppendLog("[エラー] 起動コマンドが設定されていません。", project);
            return;
        }

        try
        {
            project.IsRunningElevated = true;
            _processService.LaunchElevated(project.FolderPath, project.LaunchCommand,
                line => Application.Current.Dispatcher.Invoke(() => AppendLog(line, project)),
                running => Application.Current.Dispatcher.Invoke(() => project.IsLaunchedByBuilder = running));
            AppendCommandLog($"> {project.LaunchCommand} (管理者として実行)", project);
            AppendLog("[情報] UAC の確認画面を表示します。承認後の出力はログに表示されません。", project);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: ユーザーがUACを拒否
        {
            AppendLog("[キャンセル] 管理者権限への昇格がキャンセルされました。", project);
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}", project);
        }
    }

    /// <summary>
    /// 実行中のプロジェクトを終了→ビルド→再起動する。自分自身(Builder)は
    /// 専用のRebuildAndRestartSelfを使うため対象外。実行中プロセスの権限レベル
    /// (管理者権限かどうか)を判定し、同じ権限で再起動する(必要ならUACダイアログが表示される)。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task RebuildAndRestart()
    {
        if (SelectedProject is not { IsRunning: true, IsSelf: false } project) return;

        if (string.IsNullOrWhiteSpace(project.BuildCommand))
        {
            AppendLog("[エラー] ビルドコマンドが設定されていません。", project);
            return;
        }
        if (string.IsNullOrWhiteSpace(project.LaunchCommand))
        {
            AppendLog("[エラー] 起動コマンドが設定されていません。", project);
            return;
        }

        var state = (await Task.Run(() => _runningProcessDetector.DetectRunningProjects([project.FolderPath])))
            .GetValueOrDefault(project.FolderPath, ProjectRunState.NotRunning);

        var wasElevated = project.IsLaunchedByBuilder ? project.IsRunningElevated : state.IsElevated == true;

        AppendCommandLog("> ビルド&再起動", project);

        await KillRunningProcessesAsync(project, state.Pids);

        await RunCommandAsync(project.FolderPath, project.BuildCommand, project);

        if (wasElevated)
            LaunchProjectAsAdmin(project);
        else
            LaunchProject(project);
    }

    /// <summary>
    /// Builder自身（自分がビルドされたリポジトリ）のプロジェクトが選択されている場合のみ有効。
    /// dotnet buildは実行中のexe/pdbを上書きできず失敗するため、
    /// 「自分を終了 → ビルド → 再起動」を行う専用スクリプトをdetach起動する。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRebuildAndRestartSelf))]
    private void RebuildAndRestartSelf()
    {
        if (SelectedProject is not { IsSelf: true } project) return;

        try
        {
            var script = SelfProjectDetector.BuildRebuildRestartScript(project.FolderPath);
            _processService.LaunchPwshScriptDetached(project.FolderPath, script);
            AppendCommandLog("> ビルド&再起動", project);
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}", project);
        }
    }

    private bool CanRebuildAndRestartSelf() => SelectedProject?.IsSelf == true;

    /// <summary>
    /// 実行中のプロジェクトを終了する。自分自身(Builder)は専用の停止手順が必要なため対象外。
    /// </summary>
    [RelayCommand]
    private async Task StopProject(ProjectEntry? project)
    {
        if (project is not { IsRunning: true, IsSelf: false }) return;

        var state = (await Task.Run(() => _runningProcessDetector.DetectRunningProjects([project.FolderPath])))
            .GetValueOrDefault(project.FolderPath, ProjectRunState.NotRunning);

        AppendCommandLog("> 停止", project);

        await KillRunningProcessesAsync(project, state.Pids);
    }

    /// <summary>
    /// 指定したPIDのプロセスをプロセスツリーごと終了し、Builder側の実行中フラグをリセットする。
    /// </summary>
    private async Task KillRunningProcessesAsync(ProjectEntry project, IReadOnlyList<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                await Task.Run(() => proc.WaitForExit(10000));
                AppendLog($"[情報] プロセスを終了しました (PID {pid})", project);
            }
            catch (ArgumentException)
            {
                // 既に終了済み
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] プロセス (PID {pid}) を終了できませんでした: {ex.Message}", project);
            }
        }

        project.IsLaunchedByBuilder = false;
        project.IsDetectedExternally = false;
    }

    /// <summary>
    /// pull対象(GitBehindCount &gt; 0)があるプロジェクトすべてに対して、
    /// pull → ビルド → (実行中だった場合のみ)同じ権限レベルで再起動、を順番に行う。
    /// Builder自身は専用のRebuildAndRestartSelfがあるため対象外。
    /// </summary>
    [RelayCommand]
    private async Task BulkUpdate()
    {
        var targets = Projects.Where(p => p.IsGitRepository && p.HasBehind && !p.IsSelf).ToList();
        if (targets.Count == 0)
        {
            AppendLog("[一括更新] pull対象のプロジェクトはありません。");
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendLog($"[一括更新] {targets.Count} 件のプロジェクトを更新します。");

        try
        {
            foreach (var project in targets)
            {
                if (_cts.Token.IsCancellationRequested) break;
                await UpdateProjectAsync(project, _cts.Token);
            }

            AppendLog(_cts.Token.IsCancellationRequested ? "[一括更新] キャンセルしました。" : "[一括更新] 完了しました。");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }

        _ = RefreshAllGitSyncStatusAsync();
    }

    private async Task UpdateProjectAsync(ProjectEntry project, CancellationToken ct)
    {
        AppendLog($"[一括更新] {project.Name}", project);

        var state = await Task.Run(() => _runningProcessDetector.DetectRunningProjects([project.FolderPath]), ct);
        var runState = state.GetValueOrDefault(project.FolderPath, ProjectRunState.NotRunning);
        var wasRunning = project.IsRunning;
        var wasElevated = project.IsLaunchedByBuilder ? project.IsRunningElevated : runState.IsElevated == true;

        if (wasRunning)
        {
            await KillRunningProcessesAsync(project, runState.Pids);
        }

        if (!await RunCommandQuietAsync(project.FolderPath, "git pull", project, ct))
            return;

        if (!string.IsNullOrWhiteSpace(project.BuildCommand))
        {
            if (!await RunCommandQuietAsync(project.FolderPath, project.BuildCommand, project, ct))
                return;
        }
        else
        {
            AppendLog("[警告] ビルドコマンドが設定されていないため、ビルドをスキップしました。", project);
        }

        if (!wasRunning) return;

        if (string.IsNullOrWhiteSpace(project.LaunchCommand))
        {
            AppendLog("[警告] 起動コマンドが設定されていないため、再起動をスキップしました。", project);
            return;
        }

        if (wasElevated)
            LaunchProjectAsAdmin(project);
        else
            LaunchProject(project);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SaveProjects();
        AppendLog("[保存完了] 設定を保存しました。");
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void ClearLog()
    {
        OutputLog = string.Empty;
        if (SelectedProject != null)
            SelectedProject.Log = string.Empty;
        LogReset?.Invoke(string.Empty);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedProject == null) return;
        if (Directory.Exists(SelectedProject.FolderPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", SelectedProject.FolderPath);
        }
    }

    [RelayCommand]
    private void OpenPowerShell()
    {
        if (SelectedProject == null) return;
        if (Directory.Exists(SelectedProject.FolderPath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pwsh.exe")
            {
                WorkingDirectory = SelectedProject.FolderPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }

    [RelayCommand]
    private async Task AddAction()
    {
        if (SelectedProject == null) return;

        var view = new ActionEditDialog();
        var result = await DialogHost.Show(view, "RootDialog");

        if (result is true)
        {
            var action = new ProjectAction
            {
                Name = view.ActionName,
                Script = view.Script,
                LaunchOnly = view.LaunchOnly
            };
            SelectedProject.Actions.Add(action);
            OnPropertyChanged(nameof(SelectedProject));
            SaveProjects();
        }
    }

    [RelayCommand]
    private async Task RunAction(ProjectAction action)
    {
        if (SelectedProject == null || action == null) return;

        if (string.IsNullOrWhiteSpace(action.Script))
        {
            AppendLog($"[エラー] アクション「{action.Name}」のスクリプトが空です。");
            return;
        }

        var project = SelectedProject;

        if (action.LaunchOnly)
        {
            try
            {
                _processService.LaunchPwshScriptDetached(project.FolderPath, action.Script);
                AppendCommandLog($"> {action.Name}", project);
            }
            catch (Exception ex)
            {
                AppendLog($"[エラー] {ex.Message}", project);
            }
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendCommandLog($"> {action.Name}", project);

        try
        {
            await _processService.RunPwshScriptAsync(project.FolderPath, action.Script, line =>
            {
                Application.Current.Dispatcher.Invoke(() => AppendLog(line, project));
            }, _cts.Token);

            AppendLog("[完了]", project);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[キャンセル]", project);
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}", project);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task EditAction(ProjectAction action)
    {
        if (SelectedProject == null || action == null) return;

        var view = new ActionEditDialog
        {
            ActionName = action.Name,
            Script = action.Script,
            LaunchOnly = action.LaunchOnly
        };

        var result = await DialogHost.Show(view, "RootDialog");

        if (result is true)
        {
            action.Name = view.ActionName;
            action.Script = view.Script;
            action.LaunchOnly = view.LaunchOnly;
            OnPropertyChanged(nameof(SelectedProject));
            SaveProjects();
        }
    }

    public IEnumerable<string> ExistingGroups =>
        Projects.Select(p => p.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().OrderBy(g => g);

    [RelayCommand]
    private async Task SetGroup()
    {
        if (SelectedProject == null) return;

        var view = new SetGroupDialog { GroupName = SelectedProject.Group };
        view.SetExistingGroups(ExistingGroups);
        var result = await DialogHost.Show(view, "RootDialog");
        if (result is true)
        {
            SelectedProject.Group = view.GroupName.Trim();
            RefreshGroupTabs();
            SaveProjects();
        }
    }

    [RelayCommand]
    private async Task OpenGroupManagement()
    {
        var view = new GroupManagementDialog(Projects, ExistingGroups);
        var result = await DialogHost.Show(view, "RootDialog");
        if (result is true)
        {
            foreach (var item in view.Items)
                item.Project.Group = item.Group.Trim();
            RefreshGroupTabs();
            SaveProjects();
        }
    }

    [RelayCommand]
    private void DeleteAction(ProjectAction action)
    {
        if (SelectedProject == null || action == null) return;

        SelectedProject.Actions.Remove(action);
        OnPropertyChanged(nameof(SelectedProject));
        SaveProjects();
    }

    private async Task RunCommandAsync(string workingDir, string command, ProjectEntry? project = null)
    {
        project ??= SelectedProject;
        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            await RunCommandQuietAsync(workingDir, command, project, _cts.Token);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    /// <summary>
    /// IsBusy/_cts の管理を行わない版のコマンド実行。一括更新のように複数コマンドを
    /// 連続実行する呼び出し元が、全体を通して1つの busy 状態・キャンセルトークンを
    /// 管理できるようにするため分離している。
    /// </summary>
    private async Task<bool> RunCommandQuietAsync(string workingDir, string command, ProjectEntry? project, CancellationToken ct)
    {
        AppendCommandLog($"> {command}", project);

        try
        {
            await _processService.RunAsync(workingDir, command, line =>
            {
                Application.Current.Dispatcher.Invoke(() => AppendLog(line, project));
            }, ct);

            AppendLog("[完了]", project);
            project?.RefreshAppIcon();
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("[キャンセル]", project);
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}", project);
            return false;
        }
    }

    partial void OnSelectedProjectChanging(ProjectEntry? value)
    {
        if (_selectedProject != null)
            _selectedProject.Log = OutputLog;
    }

    partial void OnSelectedProjectChanged(ProjectEntry? value)
    {
        // Defer Refresh to avoid re-entrancy: this method can be called inside a
        // CollectionChanged event chain (e.g. ListBox.SelectionChanged during Move),
        // and calling FilteredProjects.Refresh() synchronously there causes WPF to
        // fire a nested CollectionChanged(Reset) while the first one is still being
        // dispatched, crashing the ItemContainerGenerator.
        Application.Current.Dispatcher.BeginInvoke(FilteredProjects.Refresh);
        OutputLog = value?.Log ?? string.Empty;
        LogReset?.Invoke(OutputLog);
        RefreshCurrentBranch();
        _ = RefreshBranchesAsync();
        _ = RefreshGitDirtyStatusAsync();
    }

    private async Task RefreshBranchesAsync()
    {
        Branches.Clear();
        if (SelectedProject == null || !SelectedProject.IsGitRepository) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = SelectedProject.FolderPath,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("branch");

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            foreach (var line in output.Split('\n'))
            {
                var branch = line.TrimStart('*', ' ').Trim();
                if (!string.IsNullOrEmpty(branch))
                    Branches.Add(branch);
            }
        }
        catch { }
    }

    private static async Task<string> GetGitOriginUrlAsync(string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("remote");
            psi.ArgumentList.Add("get-url");
            psi.ArgumentList.Add("origin");

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryConvertToRepositoryPageUrl(string remoteUrl, out string repositoryPageUrl)
    {
        repositoryPageUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(remoteUrl)) return false;

        var normalized = remoteUrl.Trim();
        return TryConvertToGithubPageUrl(normalized, out repositoryPageUrl) ||
               TryConvertToAzureDevOpsPageUrl(normalized, out repositoryPageUrl);
    }

    private static bool TryConvertToGithubPageUrl(string remoteUrl, out string githubUrl)
    {
        githubUrl = string.Empty;

        if (remoteUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            return TryBuildGithubPageUrl(remoteUrl["git@github.com:".Length..], out githubUrl);

        if (remoteUrl.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
            return TryBuildGithubPageUrl(remoteUrl["ssh://git@github.com/".Length..], out githubUrl);

        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return TryBuildGithubPageUrl(uri.AbsolutePath.TrimStart('/'), out githubUrl);

        return false;
    }

    private static bool TryBuildGithubPageUrl(string repoPath, out string githubUrl)
    {
        githubUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(repoPath)) return false;

        var normalized = repoPath.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        var owner = Uri.EscapeDataString(Uri.UnescapeDataString(segments[0]));
        var repo = Uri.EscapeDataString(Uri.UnescapeDataString(segments[1]));
        githubUrl = $"https://github.com/{owner}/{repo}";
        return true;
    }

    private static bool TryConvertToAzureDevOpsPageUrl(string remoteUrl, out string azureDevOpsUrl)
    {
        azureDevOpsUrl = string.Empty;

        if (remoteUrl.StartsWith("git@ssh.dev.azure.com:v3/", StringComparison.OrdinalIgnoreCase))
            return TryBuildAzureDevOpsPageUrlFromSshPath(remoteUrl["git@ssh.dev.azure.com:v3/".Length..], out azureDevOpsUrl);

        if (remoteUrl.StartsWith("ssh://git@ssh.dev.azure.com/v3/", StringComparison.OrdinalIgnoreCase))
            return TryBuildAzureDevOpsPageUrlFromSshPath(remoteUrl["ssh://git@ssh.dev.azure.com/v3/".Length..], out azureDevOpsUrl);

        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.Host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var sshSegments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (sshSegments.Length >= 4 && sshSegments[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
                return TryBuildAzureDevOpsPageUrl(sshSegments[1], sshSegments[2], sshSegments[3], out azureDevOpsUrl);
            return false;
        }

        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length >= 4 && segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
                return TryBuildAzureDevOpsPageUrl(segments[0], segments[1], segments[3], out azureDevOpsUrl);

            if (segments.Length >= 4 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
                return TryBuildAzureDevOpsPageUrl(segments[1], segments[2], segments[3], out azureDevOpsUrl);

            return false;
        }

        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && segments[1].Equals("_git", StringComparison.OrdinalIgnoreCase))
                return TryBuildVisualStudioPageUrl(uri.Host, segments[0], segments[2], out azureDevOpsUrl);

            if (segments.Length >= 4 &&
                segments[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
                return TryBuildVisualStudioPageUrl(uri.Host, segments[1], segments[3], out azureDevOpsUrl);
        }

        return false;
    }

    private static bool TryBuildAzureDevOpsPageUrlFromSshPath(string sshPath, out string azureDevOpsUrl)
    {
        azureDevOpsUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(sshPath)) return false;

        var segments = sshPath.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3) return false;

        return TryBuildAzureDevOpsPageUrl(segments[0], segments[1], segments[2], out azureDevOpsUrl);
    }

    private static bool TryBuildAzureDevOpsPageUrl(string organization, string project, string repository, out string azureDevOpsUrl)
    {
        azureDevOpsUrl = string.Empty;

        organization = Uri.UnescapeDataString(organization.Trim('/'));
        project = Uri.UnescapeDataString(project.Trim('/'));
        repository = Uri.UnescapeDataString(repository.Trim('/'));
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];

        if (string.IsNullOrWhiteSpace(organization) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repository))
            return false;

        azureDevOpsUrl =
            $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}";
        return true;
    }

    private static bool TryBuildVisualStudioPageUrl(string host, string project, string repository, out string visualStudioUrl)
    {
        visualStudioUrl = string.Empty;
        project = Uri.UnescapeDataString(project.Trim('/'));
        repository = Uri.UnescapeDataString(repository.Trim('/'));
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repository))
            return false;

        visualStudioUrl =
            $"https://{host}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}";
        return true;
    }

    private void RefreshCurrentBranch()
    {
        if (SelectedProject == null || !SelectedProject.IsGitRepository)
        {
            CurrentBranch = string.Empty;
            return;
        }

        var headFile = Path.Combine(SelectedProject.FolderPath, ".git", "HEAD");
        try
        {
            var content = File.ReadAllText(headFile).Trim();
            if (content.StartsWith("ref: refs/heads/"))
                CurrentBranch = content["ref: refs/heads/".Length..];
            else if (content.Length >= 7)
                CurrentBranch = content[..7]; // detached HEAD
            else
                CurrentBranch = content;
        }
        catch
        {
            CurrentBranch = string.Empty;
        }
    }

    private async Task RefreshGitDirtyStatusAsync()
    {
        if (SelectedProject == null || !SelectedProject.IsGitRepository)
        {
            IsGitDirty = false;
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = SelectedProject.FolderPath,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            IsGitDirty = !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            IsGitDirty = false;
        }
    }

    private async Task RefreshAllGitSyncStatusAsync()
    {
        if (!await _gitSyncLock.WaitAsync(0)) return;
        try
        {
            var tasks = Projects.ToList()
                .Where(p => p.IsGitRepository)
                .Select(RefreshGitSyncStatusForProjectAsync);
            await Task.WhenAll(tasks);
        }
        finally
        {
            _gitSyncLock.Release();
        }
    }

    private async Task RefreshGitSyncStatusForProjectAsync(ProjectEntry project)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = project.FolderPath,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("rev-list");
            psi.ArgumentList.Add("--left-right");
            psi.ArgumentList.Add("--count");
            psi.ArgumentList.Add("HEAD...@{upstream}");

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                var parts = output.Trim().Split('\t');
                project.GitAheadCount = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
                project.GitBehindCount = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
            }
            else
            {
                project.GitAheadCount = 0;
                project.GitBehindCount = 0;
            }
        }
        catch
        {
            project.GitAheadCount = 0;
            project.GitBehindCount = 0;
        }
    }

    private void AppendLog(string line) => AppendLog(line, SelectedProject);

    /// <summary>
    /// ログ行を <paramref name="owner"/> プロジェクトに追加する。owner が現在選択中のタブと
    /// 一致する場合のみ画面表示用の OutputLog / LineAppended に反映し、非選択タブで実行中の
    /// コマンド出力が別プロジェクトの表示に混入しないようにする。
    /// </summary>
    private void AppendLog(string line, ProjectEntry? owner)
    {
        if (owner == null)
        {
            OutputLog += line + Environment.NewLine;
            LineAppended?.Invoke(line);
            return;
        }

        owner.Log += line + Environment.NewLine;
        if (owner == SelectedProject)
        {
            OutputLog = owner.Log;
            LineAppended?.Invoke(line);
        }
    }

    /// <summary>
    /// コマンド行をアクセントカラーの ANSI TrueColor シーケンスで色付けして追加する。
    /// </summary>
    private void AppendCommandLog(string commandLine) => AppendCommandLog(commandLine, SelectedProject);

    private void AppendCommandLog(string commandLine, ProjectEntry? owner)
    {
        var c = ParseColor(_accentColorHex);
        AppendLog($"\x1b[38;2;{c.R};{c.G};{c.B}m{commandLine}\x1b[0m", owner);
    }
}
