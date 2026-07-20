using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Builder.Services;

/// <summary>プロジェクトフォルダに紐づく実行中プロセスの検出結果。</summary>
/// <param name="IsRunning">該当プロセスが1つ以上見つかったか。</param>
/// <param name="IsElevated">見つかったプロセスが管理者権限で動作しているか(判定不能な場合はnull)。</param>
/// <param name="Pids">見つかったプロセスID一覧。</param>
public sealed record ProjectRunState(bool IsRunning, bool? IsElevated, IReadOnlyList<int> Pids)
{
    public static readonly ProjectRunState NotRunning = new(false, null, []);
}

/// <summary>
/// WMIでOS全体のプロセス一覧を走査し、各プロジェクトのフォルダに紐づくプロセスが
/// 起動中かどうか・管理者権限で動作しているかを判定する。Builder経由で起動していない
/// (手動起動やVS等からの実行)プロセスも検出対象。
/// </summary>
public class RunningProcessDetector
{
    /// <summary>
    /// 実行ファイルのパスがフォルダ配下にある、またはコマンドラインにフォルダパスが
    /// 含まれるプロセスをフォルダごとに集計する。
    /// </summary>
    public Dictionary<string, ProjectRunState> DetectRunningProjects(IEnumerable<string> folderPaths)
    {
        var folders = folderPaths
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pidsByFolder = folders.ToDictionary(f => f, _ => new List<int>(), StringComparer.OrdinalIgnoreCase);

        if (folders.Count > 0)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process");
                using var results = searcher.Get();

                foreach (var item in results)
                {
                    using var mo = (ManagementObject)item;
                    if (mo["ProcessId"] is not uint pid || pid == 0) continue;

                    var exePath = mo["ExecutablePath"] as string;
                    var commandLine = mo["CommandLine"] as string;

                    foreach (var folder in folders)
                    {
                        var matches =
                            (!string.IsNullOrEmpty(exePath) && exePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(commandLine) && commandLine.Contains(folder, StringComparison.OrdinalIgnoreCase));

                        if (matches)
                            pidsByFolder[folder].Add((int)pid);
                    }
                }
            }
            catch
            {
                // WMIが利用できない/権限不足の場合はベストエフォート扱いとし、検出なしとする。
            }
        }

        var result = new Dictionary<string, ProjectRunState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, pids) in pidsByFolder)
        {
            if (pids.Count == 0)
            {
                result[folder] = ProjectRunState.NotRunning;
                continue;
            }

            var elevationResults = pids.Select(TryIsProcessElevated).ToList();
            bool? elevated =
                elevationResults.Any(e => e == true) ? true :
                elevationResults.All(e => e == false) ? false :
                null;

            result[folder] = new ProjectRunState(true, elevated, pids);
        }

        return result;
    }

    /// <summary>
    /// 指定PIDのプロセスが管理者権限(昇格済みトークン)で動作しているかを判定する。
    /// トークンを開けない(=ハンドル取得がアクセス拒否になる)場合は、自プロセスより
    /// 高い整合性レベルで動作している強いシグナルとみなしtrueを返す。
    /// </summary>
    private static bool? TryIsProcessElevated(int pid)
    {
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            using var process = Process.GetProcessById(pid);

            IntPtr processHandle;
            try
            {
                processHandle = process.Handle;
            }
            catch (Win32Exception)
            {
                return true;
            }

            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
                return null;

            var elevationPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenElevation, elevationPtr, sizeof(int), out _))
                    return null;

                return Marshal.ReadInt32(elevationPtr) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(elevationPtr);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
                CloseHandle(tokenHandle);
        }
    }

    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
