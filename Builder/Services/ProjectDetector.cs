using System.IO;
using System.Xml.Linq;
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

        // exe を起動する .csproj を優先し、その中でルートフォルダ名と同名フォルダ内の .csproj、
        // なければパスが短いものを優先
        var rootName = Path.GetFileName(entry.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var csproj = csprojFiles
            .OrderBy(f => IsExecutableProject(f) ? 0 : 1)
            .ThenBy(f => Path.GetFileName(Path.GetDirectoryName(f)) == rootName ? 0 : 1)
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

    private static bool IsExecutableProject(string csprojPath)
    {
        try
        {
            var document = XDocument.Load(csprojPath);
            var outputType = document
                .Descendants()
                .Where(e => e.Name.LocalName == "OutputType")
                .Select(e => e.Value.Trim())
                .FirstOrDefault();

            return string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
    // ルート直下だけでなくサブフォルダのモジュールも検出し(モノレポ構成に対応)、
    // cmd/<name>/main.go パターンがあればそれをエントリポイントとして扱う。
    // モジュールがルート直下にない場合は `-C` (Go 1.20+) でモジュールのディレクトリを指定する。
    private static bool TryDetectGo(ProjectEntry entry)
    {
        var goModFiles = Directory.GetFiles(entry.FolderPath, "go.mod", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("vendor"))
            .ToList();
        if (goModFiles.Count == 0) return false;

        var rootPath = entry.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootName = Path.GetFileName(rootPath);

        // ルート直下のモジュールを優先し、なければパスが短い(=浅い)ものを優先
        var goMod = goModFiles
            .OrderBy(f => Path.GetDirectoryName(f) == rootPath ? 0 : 1)
            .ThenBy(f => f.Length)
            .First();
        var moduleDir = Path.GetDirectoryName(goMod)!;
        var relModuleDir = Path.GetRelativePath(entry.FolderPath, moduleDir).Replace('\\', '/');

        var target = ".";
        var cmdRoot = Path.Combine(moduleDir, "cmd");
        if (Directory.Exists(cmdRoot))
        {
            var candidates = Directory.GetDirectories(cmdRoot)
                .Where(d => File.Exists(Path.Combine(d, "main.go")))
                .Select(Path.GetFileName)
                .ToList();

            if (candidates.Count > 0)
            {
                var moduleName = ReadModuleName(goMod);
                var preferred = candidates.FirstOrDefault(c => string.Equals(c, moduleName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault(c => string.Equals(c, rootName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).First();

                target = $"./cmd/{preferred}";
            }
        }

        var binName = target == "." ? rootName : Path.GetFileName(target);
        var isNested = relModuleDir != ".";

        entry.BuildCommand = isNested
            ? $"go build -C {Quote(relModuleDir)} -o {binName}.exe {target}"
            : $"go build -o {binName}.exe {target}";

        entry.LaunchCommand = isNested
            ? $"go run -C {Quote(relModuleDir)} {target}"
            : $"go run {target}";

        return true;
    }

    private static string? ReadModuleName(string goModPath)
    {
        try
        {
            var line = File.ReadLines(goModPath).FirstOrDefault(l => l.StartsWith("module "));
            if (line == null) return null;
            var name = line["module ".Length..].Trim();
            var lastSlash = name.LastIndexOf('/');
            return lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

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
