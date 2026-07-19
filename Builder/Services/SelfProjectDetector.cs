using System.IO;
using System.Runtime.CompilerServices;
using Builder.Models;

namespace Builder.Services;

/// <summary>
/// Builder自身の実行ファイル・ソースツリーを検出し、自己ビルド時のファイルロック
/// （実行中のexe/pdbをdotnet buildが上書きできない問題）を回避する
/// 「終了 → ビルド → 再起動」スクリプトを組み立てる。
/// </summary>
public static class SelfProjectDetector
{
    // コンパイル時にこのファイルの絶対パスが埋め込まれる（ビルドしたマシン上でのみ有効）。
    private static string ThisSourceFilePath([CallerFilePath] string path = "") => path;

    private static readonly Lazy<string?> RepoRootLazy = new(FindRepoRoot);

    private static string? FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(ThisSourceFilePath());
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static bool IsSelf(ProjectEntry project)
    {
        if (string.IsNullOrWhiteSpace(project.FolderPath) || RepoRootLazy.Value == null)
            return false;

        var a = Path.GetFullPath(project.FolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(RepoRootLazy.Value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 実行中のBuilder.exeを終了 → dotnet build → 再起動、を行うpwshスクリプトを生成する。
    /// ビルド失敗時は（ロック解除により生成された）既存exeをそのまま再起動し、
    /// 起動不能になる事故を防ぐ。
    /// </summary>
    public static string BuildRebuildRestartScript(string workingDirectory)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("実行中の exe パスを取得できませんでした。");
        var logFile = Path.Combine(Path.GetTempPath(), "Builder-rebuild.log");

        return $$"""
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $exePath = '{{Escape(exePath)}}'
            $logFile = '{{Escape(logFile)}}'

            "[$(Get-Date -Format o)] rebuild-restart start" | Out-File -FilePath $logFile

            Get-Process -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq $exePath } |
                ForEach-Object { $_ | Stop-Process -Force; $_ | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }

            Set-Location '{{Escape(workingDirectory)}}'
            dotnet build *>> $logFile

            if ($LASTEXITCODE -eq 0) {
                "[$(Get-Date -Format o)] build OK, restarting" | Out-File -FilePath $logFile -Append
            } else {
                "[$(Get-Date -Format o)] build FAILED (exit $LASTEXITCODE), restarting old exe" | Out-File -FilePath $logFile -Append
            }

            if (Test-Path $exePath) {
                Start-Process -FilePath $exePath
            }
            """;
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
