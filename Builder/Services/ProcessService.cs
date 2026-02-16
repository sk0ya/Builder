using System.Diagnostics;
using System.Text;

namespace Builder.Services;

public class ProcessService
{
    public async Task RunAsync(string workingDirectory, string command, Action<string> onOutput, CancellationToken ct = default)
    {
        var parts = ParseCommand(command);
        if (parts.Length == 0) return;

        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = parts.Length > 1 ? string.Join(' ', parts[1..]) : "",
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
            if (e.Data != null) onOutput($"[ERR] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            onOutput($"[Process exited with code {process.ExitCode}]");
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
