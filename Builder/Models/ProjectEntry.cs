using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Builder.Services;
using MaterialDesignThemes.Wpf;

namespace Builder.Models;

public class ProjectEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string BuildCommand { get; set; } = string.Empty;
    public string LaunchCommand { get; set; } = string.Empty;

    public ObservableCollection<ProjectAction> Actions { get; set; } = [];

    [JsonIgnore]
    public string Log { get; set; } = string.Empty;

    private bool _isLaunchedByBuilder;
    private bool _isDetectedExternally;

    /// <summary>
    /// Builderから起動して実行中に加え、外部(手動起動やVS等)から起動され
    /// WMIスキャンで検出されたプロセスも含めた実行中判定。
    /// </summary>
    [JsonIgnore]
    public bool IsRunning => _isLaunchedByBuilder || _isDetectedExternally;

    [JsonIgnore]
    public bool IsLaunchedByBuilder
    {
        get => _isLaunchedByBuilder;
        set
        {
            if (_isLaunchedByBuilder == value) return;
            _isLaunchedByBuilder = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRebuildAndRestart)));
        }
    }

    [JsonIgnore]
    public bool IsDetectedExternally
    {
        get => _isDetectedExternally;
        set
        {
            if (_isDetectedExternally == value) return;
            _isDetectedExternally = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRebuildAndRestart)));
        }
    }

    private bool _isRunningElevated;

    /// <summary>
    /// 実行中プロセスが管理者権限で動作しているか。Builderから起動した場合は
    /// 起動時に確実な値を設定し、外部起動の場合はWMIスキャンでの推定値を用いる。
    /// ビルド&amp;再起動時にこの値を見て、同じ権限レベルで再起動する。
    /// </summary>
    [JsonIgnore]
    public bool IsRunningElevated
    {
        get => _isRunningElevated;
        set => SetField(ref _isRunningElevated, value);
    }

    [JsonIgnore]
    public bool IsGitRepository => Directory.Exists(Path.Combine(FolderPath, ".git"));

    [JsonIgnore]
    public bool IsSelf => SelfProjectDetector.IsSelf(this);

    /// <summary>
    /// 汎用「ビルド&amp;再起動」ボタンの表示条件。Builder自身は専用のボタン
    /// (RebuildAndRestartSelf)を使うため対象外とする。
    /// </summary>
    [JsonIgnore]
    public bool CanRebuildAndRestart => IsRunning && !IsSelf;

    private int _gitAheadCount;
    private int _gitBehindCount;

    [JsonIgnore]
    public int GitAheadCount
    {
        get => _gitAheadCount;
        set
        {
            if (SetField(ref _gitAheadCount, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAhead)));
        }
    }

    [JsonIgnore]
    public int GitBehindCount
    {
        get => _gitBehindCount;
        set
        {
            if (SetField(ref _gitBehindCount, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBehind)));
        }
    }

    [JsonIgnore]
    public bool HasAhead => _gitAheadCount > 0;

    [JsonIgnore]
    public bool HasBehind => _gitBehindCount > 0;

    [JsonIgnore]
    public PackIconKind IconKind => _iconKind ??= DetectIconKind();

    [JsonIgnore]
    public ImageSource? AppIcon
    {
        get
        {
            if (!_appIconLoaded)
            {
                _appIcon = ProjectIconService.Load(this);
                _appIconLoaded = true;
            }

            return _appIcon;
        }
    }

    [JsonIgnore]
    public bool HasAppIcon => AppIcon != null;

    private PackIconKind? _iconKind;
    private ImageSource? _appIcon;
    private bool _appIconLoaded;

    /// <summary>
    /// ビルド後など、プロジェクト内に新しい実行ファイルが生成されたときに
    /// 一覧のアイコンを再探索します。
    /// </summary>
    public void RefreshAppIcon()
    {
        var oldIcon = _appIcon;
        _appIcon = ProjectIconService.Load(this);
        _appIconLoaded = true;
        if (!ReferenceEquals(oldIcon, _appIcon))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAppIcon)));
        }
    }

    private PackIconKind DetectIconKind()
    {
        if (!Directory.Exists(FolderPath)) return PackIconKind.CodeBraces;

        // .csproj/.sln → C#（.sln を先に確認してショートサーキット、なければ .csproj をサブディレクトリまで探す）
        if (Directory.GetFiles(FolderPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.GetFiles(FolderPath, "*.csproj", SearchOption.AllDirectories).Length > 0)
            return PackIconKind.LanguageCsharp;

        var topFiles = Directory.GetFiles(FolderPath, "*", SearchOption.TopDirectoryOnly)
                                .Select(f => Path.GetFileName(f))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // tsconfig.json → TypeScript
        if (topFiles.Contains("tsconfig.json"))
            return PackIconKind.LanguageTypescript;

        // package.json → Node.js
        if (topFiles.Contains("package.json"))
            return PackIconKind.Nodejs;

        // Python
        if (topFiles.Contains("requirements.txt") || topFiles.Contains("setup.py") || topFiles.Contains("pyproject.toml"))
            return PackIconKind.LanguagePython;

        // Go
        if (topFiles.Contains("go.mod"))
            return PackIconKind.LanguageGo;

        // Rust
        if (topFiles.Contains("Cargo.toml"))
            return PackIconKind.LanguageRust;

        // Java (Maven / Gradle)
        if (topFiles.Contains("pom.xml") || topFiles.Contains("build.gradle") || topFiles.Contains("build.gradle.kts"))
            return PackIconKind.LanguageJava;

        // Ruby
        if (topFiles.Contains("Gemfile"))
            return PackIconKind.LanguageRuby;

        // PHP
        if (Directory.GetFiles(FolderPath, "*.php", SearchOption.TopDirectoryOnly).Length > 0)
            return PackIconKind.LanguagePhp;

        return PackIconKind.CodeBraces;
    }
}
