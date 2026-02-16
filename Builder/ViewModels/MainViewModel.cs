using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Builder.Models;
using Builder.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Builder.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly ProcessService _processService = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ProjectEntry> Projects { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GitPullCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProjectCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedProject))]
    private ProjectEntry? _selectedProject;

    [ObservableProperty]
    private string _outputLog = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelectedProject => SelectedProject != null;

    public MainViewModel()
    {
        LoadProjects();
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
        _settingsService.Save(new AppSettings { Projects = [.. Projects] });
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
            Projects.Add(entry);
            SelectedProject = entry;
            SaveProjects();
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
    private async Task GitPull()
    {
        if (SelectedProject == null) return;

        if (!SelectedProject.IsGitRepository)
        {
            AppendLog("[エラー] このフォルダはGitリポジトリではありません。");
            return;
        }

        await RunCommandAsync(SelectedProject.FolderPath, "git pull");
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
            AppendLog($"[起動] {SelectedProject.LaunchCommand}");
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

    private async Task RunCommandAsync(string workingDir, string command)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendLog($"> {command}");

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

    private void AppendLog(string line)
    {
        OutputLog += line + Environment.NewLine;
    }
}
