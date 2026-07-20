using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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
        }
    }

    [JsonIgnore]
    public bool IsGitRepository => Directory.Exists(Path.Combine(FolderPath, ".git"));

    [JsonIgnore]
    public bool IsSelf => SelfProjectDetector.IsSelf(this);

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

    private PackIconKind? _iconKind;

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
