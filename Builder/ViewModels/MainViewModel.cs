using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
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

    private string _backgroundColorHex = "#1E1E1E";
    private string _accentColorHex = "#4FC3F7";

    public bool HasSelectedProject => SelectedProject != null;

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

        var res = Application.Current.Resources;
        res["MaterialDesignPaper"] = new SolidColorBrush(bgColor);
        res["MaterialDesignCardBackground"] = new SolidColorBrush(AdjustBrightness(bgColor, 10));
        res["MaterialDesignToolBarBackground"] = new SolidColorBrush(AdjustBrightness(bgColor, -5));
        res["PrimaryHueMidBrush"] = new SolidColorBrush(accentColor);
        res["PrimaryHueMidForegroundBrush"] = new SolidColorBrush(Colors.White);
        res["SecondaryHueMidBrush"] = new SolidColorBrush(accentColor);
        res["ThemeTitleBar"] = new SolidColorBrush(AdjustBrightness(bgColor, -5));
        res["ThemeSidebar"] = new SolidColorBrush(AdjustBrightness(bgColor, 10));
        res["ThemeOutput"] = new SolidColorBrush(AdjustBrightness(bgColor, -5));
        res["ThemeAccent"] = new SolidColorBrush(accentColor);
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

        dialog.ShowDialog();
        // ダイアログ閉じたら最終状態を保存
        _backgroundColorHex = dialog.BackgroundColorHex;
        _accentColorHex = dialog.AccentColorHex;
        SaveProjects();
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

    [RelayCommand]
    private void AddAction()
    {
        if (SelectedProject == null) return;

        var dialog = new ActionEditDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            var action = new ProjectAction
            {
                Name = dialog.ActionName,
                Script = dialog.Script
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

        IsBusy = true;
        _cts = new CancellationTokenSource();
        AppendLog($"> [アクション] {action.Name}");

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
    private void EditAction(ProjectAction action)
    {
        if (SelectedProject == null || action == null) return;

        var dialog = new ActionEditDialog
        {
            Owner = Application.Current.MainWindow,
            ActionName = action.Name,
            Script = action.Script
        };

        if (dialog.ShowDialog() == true)
        {
            action.Name = dialog.ActionName;
            action.Script = dialog.Script;
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
