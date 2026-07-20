using System.Management;

namespace Builder.Services;

/// <summary>
/// WMIでOS全体のプロセス一覧を走査し、各プロジェクトのフォルダに紐づくプロセスが
/// 起動中かどうかを判定する。Builder経由で起動していない(手動起動やVS等からの実行)
/// プロセスも検出対象。
/// </summary>
public class RunningProcessDetector
{
    /// <summary>
    /// 実行ファイルのパスがフォルダ配下にある、またはコマンドラインにフォルダパスが
    /// 含まれるプロセスが存在するフォルダの集合を返す。
    /// </summary>
    public HashSet<string> DetectRunningFolders(IEnumerable<string> folderPaths)
    {
        var folders = folderPaths
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folders.Count == 0) return running;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process");
            using var results = searcher.Get();

            foreach (var item in results)
            {
                using var mo = (ManagementObject)item;
                var exePath = mo["ExecutablePath"] as string;
                var commandLine = mo["CommandLine"] as string;

                foreach (var folder in folders)
                {
                    if (running.Contains(folder)) continue;

                    var matches =
                        (!string.IsNullOrEmpty(exePath) && exePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(commandLine) && commandLine.Contains(folder, StringComparison.OrdinalIgnoreCase));

                    if (matches)
                        running.Add(folder);
                }
            }
        }
        catch
        {
            // WMIが利用できない/権限不足の場合はベストエフォート扱いとし、検出なしとする。
        }

        return running;
    }
}
