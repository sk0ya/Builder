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
        // 今後: TryDetectNode(entry), TryDetectPython(entry), ...
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

        entry.BuildCommand = "dotnet build";
        entry.LaunchCommand = relativePath.Contains(' ')
            ? $"dotnet run --project \"{relativePath}\""
            : $"dotnet run --project {relativePath}";

        return true;
    }
}
