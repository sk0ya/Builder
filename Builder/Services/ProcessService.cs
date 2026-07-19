using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Builder.Services;

public class ProcessService
{
    /// <summary>
    /// すべての pwsh スクリプト先頭に挿入する ANSI 有効化プリアンブル。
    /// $PSStyle.OutputRendering = 'ANSI' で PowerShell 自身の出力を ANSI 化し、
    /// 環境変数で各ツールの ANSI 出力を有効化する。
    /// </summary>
    private const string AnsiPreamble =
        "$PSStyle.OutputRendering = 'ANSI'\n" +
        "$env:FORCE_COLOR = '1'\n" +
        "$env:CLICOLOR_FORCE = '1'\n" +
        "$env:COLORTERM = 'truecolor'\n" +
        "$env:CARGO_TERM_COLOR = 'always'\n" +
        "$env:TERM = 'xterm-256color'\n" +
        // .NET 6+ : Console.ForegroundColor 等がリダイレクト時でも ANSI シーケンスを
        // ストリームに書き込むようにする。-clp:ForceConsoleColor との組み合わせで
        // dotnet build / MSBuild の色付き出力が有効になる。
        "$env:DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION = '1'\n";

    /// <summary>
    /// コマンドを pwsh 経由で実行し、出力をコールバックに流す。
    /// dotnet build/test 等は MSBuild の ForceConsoleColor を自動付与して色付き出力にする。
    /// </summary>
    public async Task RunAsync(string workingDirectory, string command, Action<string> onOutput, CancellationToken ct = default)
    {
        var script = AnsiPreamble + BuildCommandInvocation(command);
        await RunScriptCoreAsync(workingDirectory, script, onOutput, ct);
    }

    // CreateProcess が管理者権限必須のプロセスを起動しようとした際に返す Win32 エラーコード。
    private const int ErrorElevationRequired = 740;

    /// <summary>
    /// 起動コマンドをバックグラウンドで実行する。
    /// dotnet run のように、起動対象自体は昇格不要でも内部で起動する
    /// 実行ファイルが管理者権限を要求するケースがあるため、標準出力/標準エラーを
    /// キャプチャして onOutput に流す（そうしないと失敗時に何もログに残らない）。
    /// コマンド自体（parts[0]）が管理者権限を要求する場合は CreateProcess が
    /// ERROR_ELEVATION_REQUIRED で失敗するため、UAC 経由での起動にフォールバックする。
    /// </summary>
    public void LaunchDetached(string workingDirectory, string command, Action<string>? onOutput = null)
    {
        var parts = ParseCommand(command);
        if (parts.Length == 0) return;

        var arguments = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";

        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("プロセスを起動できませんでした。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            onOutput?.Invoke("[警告] 管理者権限が必要なため、UAC の確認画面を表示します（承認後の出力はログに表示されません）。");
            Process.Start(new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                Verb = "runas"
            });
            return;
        }

        if (onOutput == null)
        {
            process.Dispose();
            return;
        }

        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _ = MonitorDetachedAsync(process, onOutput);
    }

    private static async Task MonitorDetachedAsync(Process process, Action<string> onOutput)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                onOutput($"[Process exited with code {process.ExitCode}]");
        }
        catch (Exception ex)
        {
            onOutput($"[エラー] {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// 起動コマンドを管理者権限（UAC 昇格）で起動する。
    /// ShellExecute 経由になるため標準出力/標準エラーはキャプチャできない。
    /// </summary>
    public void LaunchElevated(string workingDirectory, string command, Action<string>? onOutput = null)
    {
        var parts = ParseCommand(command);
        if (parts.Length == 0) return;

        var arguments = parts.Length > 1 ? string.Join(' ', DisableDotnetBuildServers(parts)) : "";

        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var process = Process.Start(psi);

        // 標準出力はキャプチャできないため（昇格プロセスとの間でパイプを共有できない）、
        // せめて終了だけは検知してログに残す。ウィンドウが無いままプロセスが
        // 見えなくなる（実質的なゾンビ化）のを防ぐため。
        if (process != null && onOutput != null)
            _ = MonitorElevatedAsync(process, onOutput);
    }

    /// <summary>
    /// dotnet build/test/publish/restore/run を管理者権限で実行すると、MSBuild のノード再利用や
    /// Roslyn の共有コンパイラサーバー（VBCSCompiler.exe）が管理者権限のまま常駐プロセスとして
    /// 残ってしまう。Builder は非管理者権限のため、これらを検知・終了させる手段が無いので、
    /// そもそも常駐プロセスを生成させないようにフラグを付与する。
    /// </summary>
    private static string[] DisableDotnetBuildServers(string[] parts)
    {
        var args = parts[1..];

        if (!parts[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            args.Length == 0 ||
            args[0].ToLowerInvariant() is not ("build" or "test" or "publish" or "restore" or "run"))
        {
            return args;
        }

        return [.. args, "-nodeReuse:false", "-p:UseSharedCompilation=false"];
    }

    private static async Task MonitorElevatedAsync(Process process, Action<string> onOutput)
    {
        try
        {
            await process.WaitForExitAsync();
            onOutput($"[終了] 管理者権限で起動したプロセスが終了しました (終了コード: {process.ExitCode})");
        }
        finally
        {
            process.Dispose();
        }
    }

    public void LaunchPwshScriptDetached(string workingDirectory, string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-ExecutionPolicy Bypass -EncodedCommand {encoded}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        Process.Start(psi);
    }

    /// <summary>
    /// ユーザー定義の pwsh スクリプトを実行する。
    /// ANSI プリアンブルを先頭に自動挿入するため、スクリプト内で色付き出力が使える。
    /// </summary>
    public async Task RunPwshScriptAsync(string workingDirectory, string script, Action<string> onOutput, CancellationToken ct = default)
    {
        var fullScript = AnsiPreamble + script;
        await RunScriptCoreAsync(workingDirectory, fullScript, onOutput, ct);
    }

    /// <summary>
    /// コマンド文字列を pwsh スクリプトの呼び出し行に変換する。
    /// dotnet build 系は -tl:off -clp:ForceConsoleColor を付与して ANSI カラーを強制する。
    /// </summary>
    private static string BuildCommandInvocation(string command)
    {
        var parts = ParseCommand(command);
        if (parts.Length == 0) return "";

        var exe = parts[0];
        var args = parts.Length > 1 ? parts[1..] : [];

        // dotnet build / test / publish / restore / run → MSBuild コンソールログの強制カラー
        // -tl:off  : .NET 8+ のターミナルロガーを無効化（リダイレクト時は非対応のため）
        // -clp:ForceConsoleColor : 旧コンソールロガーで ANSI を強制出力
        if (exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            args.Length > 0 &&
            args[0].ToLowerInvariant() is "build" or "test" or "publish" or "restore" or "run")
        {
            args = [.. args, "-tl:off", "-clp:ForceConsoleColor"];
        }

        // PowerShell の & 演算子で安全に呼び出す（引数はシングルクォートでエスケープ）
        // 2>&1 は使わない: PowerShell が stderr を ErrorRecord にラップすると
        // ANSI シーケンスが壊れるため、stdout/stderr は C# 側で別々に捕捉する。
        var sb = new StringBuilder();
        sb.Append($"& '{EscapePwsh(exe)}'");
        foreach (var arg in args)
            sb.Append($" '{EscapePwsh(arg)}'");
        sb.AppendLine();
        sb.AppendLine("exit $LASTEXITCODE");
        return sb.ToString();
    }

    private static string EscapePwsh(string s) => s.Replace("'", "''");

    private static async Task RunScriptCoreAsync(string workingDirectory, string script, Action<string> onOutput, CancellationToken ct = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"builder_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tempFile, script, Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-ExecutionPolicy Bypass -File \"{tempFile}\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) onOutput(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) onOutput(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);
            process.WaitForExit();

            if (process.ExitCode != 0)
                onOutput($"[Process exited with code {process.ExitCode}]");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static string[] ParseCommand(string command)
    {
        var args = new List<string>();
        var current = "";
        var inQuotes = false;

        foreach (var c in command)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current);
                    current = "";
                }
            }
            else
            {
                current += c;
            }
        }

        if (current.Length > 0)
            args.Add(current);

        return args.ToArray();
    }
}
