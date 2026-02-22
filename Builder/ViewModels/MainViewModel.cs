using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
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
    private CancellationTokenSource? _cts;

    /// <summary>新しいログ行が追加されたときに発火（行テキストのみ、改行なし）</summary>
    public event Action<string>? LineAppended;

    /// <summary>ログがリセットされたときに発火（プロジェクト切替・クリア時）。引数は新しい全文。</summary>
    public event Action<string>? LogReset;

    public ObservableCollection<ProjectEntry> Projects { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GitFetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(GitPullCommand))]
    [NotifyCanExecuteChangedFor(nameof(GitPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenGithubPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProjectCommand))]
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

    private string _backgroundColorHex = "#1E1E1E";
    private string _accentColorHex = "#4FC3F7";

    public bool HasSelectedProject => SelectedProject != null;

    public ObservableCollection<string> Branches { get; } = [];

    public MainViewModel()
    {
        LoadProjects();
        LoadTheme();
    }

    private void LoadTheme()
    {
        var settings = _settingsService.Load();
        _backgroundColorHex = settings.BackgroundColor;
        _accentColorHex = settings.AccentColor;
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
        var settings = _settingsService.Load();
        Projects.Clear();
        foreach (var p in settings.Projects)
            Projects.Add(p);
    }

    private void SaveProjects()
    {
        var settings = _settingsService.Load();
        settings.Projects = [.. Projects];
        settings.BackgroundColor = _backgroundColorHex;
        settings.AccentColor = _accentColorHex;
        _settingsService.Save(settings);
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
        var settings = _settingsService.Load();
        var view = new GitCloneDialog(settings.LastCloneParentFolder);
        var result = await DialogHost.Show(view, "RootDialog");
        if (result is not true) return;

        var url = view.RepoUrl;
        var destPath = view.DestinationPath;

        // 親フォルダを保存
        settings.LastCloneParentFolder = view.ParentFolder;
        _settingsService.Save(settings);
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

    private async Task RunGitCloneAsync(string url, string destPath, string projectName)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendCommandLog($"> git clone {url}");
        AppendCommandLog($"  → {destPath}");

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
                if (e.Data != null) Application.Current.Dispatcher.Invoke(() => AppendLog(e.Data));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Application.Current.Dispatcher.Invoke(() => AppendLog(e.Data));
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_cts.Token);
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
                    SelectedProject = entry;
                    SaveProjects();
                });
                AppendLog($"[完了] プロジェクト「{projectName}」を追加しました。");
            }
            else if (process.ExitCode != 0)
            {
                AppendLog($"[エラー] git clone が失敗しました (終了コード: {process.ExitCode})");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[キャンセル]");
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
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

        if (string.IsNullOrWhiteSpace(SelectedProject.LaunchCommand))
        {
            AppendLog("[エラー] 起動コマンドが設定されていません。");
            return;
        }

        try
        {
            _processService.LaunchDetached(SelectedProject.FolderPath, SelectedProject.LaunchCommand);
            AppendCommandLog($"> {SelectedProject.LaunchCommand}");
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}");
        }
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

        if (action.LaunchOnly)
        {
            try
            {
                _processService.LaunchPwshScriptDetached(SelectedProject.FolderPath, action.Script);
                AppendCommandLog($"> {action.Name}");
            }
            catch (Exception ex)
            {
                AppendLog($"[エラー] {ex.Message}");
            }
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendCommandLog($"> {action.Name}");

        try
        {
            await _processService.RunPwshScriptAsync(SelectedProject.FolderPath, action.Script, line =>
            {
                Application.Current.Dispatcher.Invoke(() => AppendLog(line));
            }, _cts.Token);

            AppendLog("[完了]");
        }
        catch (OperationCanceledException)
        {
            AppendLog("[キャンセル]");
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}");
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

    [RelayCommand]
    private void DeleteAction(ProjectAction action)
    {
        if (SelectedProject == null || action == null) return;

        SelectedProject.Actions.Remove(action);
        OnPropertyChanged(nameof(SelectedProject));
        SaveProjects();
    }

    private async Task RunCommandAsync(string workingDir, string command)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendCommandLog($"> {command}");

        try
        {
            await _processService.RunAsync(workingDir, command, line =>
            {
                Application.Current.Dispatcher.Invoke(() => AppendLog(line));
            }, _cts.Token);

            AppendLog("[完了]");
        }
        catch (OperationCanceledException)
        {
            AppendLog("[キャンセル]");
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    partial void OnSelectedProjectChanging(ProjectEntry? value)
    {
        if (_selectedProject != null)
            _selectedProject.Log = OutputLog;
    }

    partial void OnSelectedProjectChanged(ProjectEntry? value)
    {
        OutputLog = value?.Log ?? string.Empty;
        LogReset?.Invoke(OutputLog);
        RefreshCurrentBranch();
        _ = RefreshBranchesAsync();
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

    private void AppendLog(string line)
    {
        OutputLog += line + Environment.NewLine;
        if (SelectedProject != null)
            SelectedProject.Log = OutputLog;
        LineAppended?.Invoke(line);
    }

    /// <summary>
    /// コマンド行をアクセントカラーの ANSI TrueColor シーケンスで色付けして追加する。
    /// </summary>
    private void AppendCommandLog(string commandLine)
    {
        var c = ParseColor(_accentColorHex);
        AppendLog($"\x1b[38;2;{c.R};{c.G};{c.B}m{commandLine}\x1b[0m");
    }
}
