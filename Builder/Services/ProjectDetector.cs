using System.IO;
using Builder.Models;

namespace Builder.Services;

/// <summary>
/// フォルダ内のプロジェクトファイルを検出し、ビルド・起動コマンドの既定値を設定します。
/// 新しいプロジェクト種別を追加する場合は DetectAndApply メソッドに追記してください。
/// </summary>
public static class ProjectDetector
{
    public static void DetectAndApply(ProjectEntry entry)
    {
        if (!Directory.Exists(entry.FolderPath)) return;

        if (TryDetectDotNet(entry)) return;
        if (TryDetectNode(entry)) return;
        if (TryDetectPython(entry)) return;
        if (TryDetectGo(entry)) return;
        if (TryDetectRust(entry)) return;
        if (TryDetectJava(entry)) return;
    }

    // .NET (*.csproj)
    private static bool TryDetectDotNet(ProjectEntry entry)
    {
        var csprojFiles = Directory.GetFiles(entry.FolderPath, "*.csproj", SearchOption.AllDirectories);
        if (csprojFiles.Length == 0) return false;

        // ルートフォルダ名と同名フォルダ内の .csproj を優先、なければパスが短いものを優先
        var rootName = Path.GetFileName(entry.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var csproj = csprojFiles
            .OrderBy(f => Path.GetFileName(Path.GetDirectoryName(f)) == rootName ? 0 : 1)
            .ThenBy(f => f.Length)
            .First();
        var relativePath = Path.GetRelativePath(entry.FolderPath, csproj)
                               .Replace('\\', '/');

        // .sln がルートにある場合はソリューション単位でビルド
        // .csproj がルート直下の場合も引数不要
        // サブフォルダにある場合はパスを指定しないとビルドできないため相対パスを付与
        var hasSln = Directory.GetFiles(entry.FolderPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0;
        var isAtRoot = !relativePath.Contains('/');
        if (hasSln || isAtRoot)
        {
            entry.BuildCommand = "dotnet build";
        }
        else
        {
            entry.BuildCommand = relativePath.Contains(' ')
                ? $"dotnet build \"{relativePath}\""
                : $"dotnet build {relativePath}";
        }

        entry.LaunchCommand = relativePath.Contains(' ')
            ? $"dotnet run --project \"{relativePath}\""
            : $"dotnet run --project {relativePath}";

        return true;
    }

    // Node.js (package.json)
    private static bool TryDetectNode(ProjectEntry entry)
    {
        var packageJson = Path.Combine(entry.FolderPath, "package.json");
        if (!File.Exists(packageJson)) return false;

        // TypeScript プロジェクトはビルドあり
        var hasTs = File.Exists(Path.Combine(entry.FolderPath, "tsconfig.json"));
        entry.BuildCommand = hasTs ? "npm run build" : "";
        entry.LaunchCommand = "npm start";

        return true;
    }

    // Python (requirements.txt / pyproject.toml / setup.py)
    private static bool TryDetectPython(ProjectEntry entry)
    {
        var hasReqs    = File.Exists(Path.Combine(entry.FolderPath, "requirements.txt"));
        var hasPyproj  = File.Exists(Path.Combine(entry.FolderPath, "pyproject.toml"));
        var hasSetup   = File.Exists(Path.Combine(entry.FolderPath, "setup.py"));
        if (!hasReqs && !hasPyproj && !hasSetup) return false;

        entry.BuildCommand = "";
        // manage.py があれば Django と判断
        if (File.Exists(Path.Combine(entry.FolderPath, "manage.py")))
            entry.LaunchCommand = "python manage.py runserver";
        else
            entry.LaunchCommand = "python main.py";

        return true;
    }

    // Go (go.mod)
    private static bool TryDetectGo(ProjectEntry entry)
    {
        if (!File.Exists(Path.Combine(entry.FolderPath, "go.mod"))) return false;

        entry.BuildCommand = "go build ./...";
        entry.LaunchCommand = "go run .";

        return true;
    }

    // Rust (Cargo.toml)
    private static bool TryDetectRust(ProjectEntry entry)
    {
        if (!File.Exists(Path.Combine(entry.FolderPath, "Cargo.toml"))) return false;

        entry.BuildCommand = "cargo build";
        entry.LaunchCommand = "cargo run";

        return true;
    }

    // Java: Maven (pom.xml) / Gradle (build.gradle)
    private static bool TryDetectJava(ProjectEntry entry)
    {
        if (File.Exists(Path.Combine(entry.FolderPath, "pom.xml")))
        {
            entry.BuildCommand = "mvn package";
            entry.LaunchCommand = "mvn exec:java";
            return true;
        }

        if (File.Exists(Path.Combine(entry.FolderPath, "build.gradle")) ||
            File.Exists(Path.Combine(entry.FolderPath, "build.gradle.kts")))
        {
            entry.BuildCommand = "gradle build";
            entry.LaunchCommand = "gradle run";
            return true;
        }

        return false;
    }
}
