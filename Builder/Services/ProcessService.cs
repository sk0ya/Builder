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

    public void LaunchDetached(string workingDirectory, string command)
    {
        var parts = ParseCommand(command);
        if (parts.Length == 0) return;

        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = parts.Length > 1 ? string.Join(' ', parts[1..]) : "",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process.Start(psi);
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
