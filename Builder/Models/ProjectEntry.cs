using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using MaterialDesignThemes.Wpf;

namespace Builder.Models;

public class ProjectEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string BuildCommand { get; set; } = string.Empty;
    public string LaunchCommand { get; set; } = string.Empty;

    public ObservableCollection<ProjectAction> Actions { get; set; } = [];

    [JsonIgnore]
    public string Log { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsGitRepository => Directory.Exists(Path.Combine(FolderPath, ".git"));

    [JsonIgnore]
    public PackIconKind IconKind
    {
        get
        {
            if (!Directory.Exists(FolderPath)) return PackIconKind.CodeBraces;

            // .csproj/.sln → C#
            if (Directory.GetFiles(FolderPath, "*.csproj", SearchOption.AllDirectories).Length > 0 ||
                Directory.GetFiles(FolderPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
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
}
