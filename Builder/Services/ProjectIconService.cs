using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Builder.Models;

namespace Builder.Services;

/// <summary>
/// プロジェクトからアプリ固有のアイコンを探して読み込みます。
/// アイコンが見つからないプロジェクトは、従来の言語アイコンにフォールバックします。
/// </summary>
public static class ProjectIconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    private static readonly Regex ExecutableTokenRegex = new(
        "(?:\\\"(?<path>[^\\\"]+\\.(?:exe|dll))\\\"|(?<path>[^\\s\\\"']+\\.(?:exe|dll)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] KnownIconNames =
    [
        "icon.ico", "app.ico", "application.ico", "favicon.ico",
        "icon.png", "app.png", "application.png", "favicon.png"
    ];

    public static ImageSource? Load(ProjectEntry project)
    {
        if (!Directory.Exists(project.FolderPath)) return null;

        foreach (var executable in GetCommandExecutables(project))
        {
            var icon = LoadExecutableIcon(executable);
            if (icon != null) return icon;
        }

        foreach (var iconPath in GetDeclaredIconFiles(project))
        {
            var icon = LoadImageFile(iconPath);
            if (icon != null) return icon;
        }

        foreach (var executable in GetProjectExecutables(project.FolderPath))
        {
            var icon = LoadExecutableIcon(executable);
            if (icon != null) return icon;
        }

        foreach (var iconPath in GetKnownIconFiles(project.FolderPath))
        {
            var icon = LoadImageFile(iconPath);
            if (icon != null) return icon;
        }

        return null;
    }

    private static IEnumerable<string> GetCommandExecutables(ProjectEntry project)
    {
        if (string.IsNullOrWhiteSpace(project.LaunchCommand)) yield break;

        var paths = new List<string>();
        try
        {
            foreach (Match match in ExecutableTokenRegex.Matches(project.LaunchCommand))
            {
                var token = Environment.ExpandEnvironmentVariables(match.Groups["path"].Value);
                var path = Path.IsPathRooted(token) ? token : Path.Combine(project.FolderPath, token);
                path = Path.GetFullPath(path);

                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, ".exe");

                if (File.Exists(path)) paths.Add(path);
            }
        }
        catch
        {
            // 不正な起動コマンドでも、他のアイコン候補の探索は続けます。
        }

        foreach (var path in paths) yield return path;
    }

    private static IEnumerable<string> GetDeclaredIconFiles(ProjectEntry project)
    {
        foreach (var csproj in EnumerateFiles(project.FolderPath, "*.csproj"))
        {
            var paths = new List<string>();
            try
            {
                var document = XDocument.Load(csproj);
                foreach (var value in document.Descendants()
                             .Where(e => e.Name.LocalName.Equals("ApplicationIcon", StringComparison.OrdinalIgnoreCase))
                             .Select(e => e.Value.Trim()))
                {
                    var path = ResolveProjectPath(project.FolderPath, Path.GetDirectoryName(csproj), value);
                    if (path != null) paths.Add(path);
                }
            }
            catch
            {
                // 壊れた/読み取り中のプロジェクトファイルは候補から除外します。
            }

            foreach (var path in paths) yield return path;
        }

        var packageJson = Path.Combine(project.FolderPath, "package.json");
        if (File.Exists(packageJson))
        {
            var paths = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
                var root = document.RootElement;

                foreach (var value in GetStringProperties(root, "icon"))
                {
                    var path = ResolveProjectPath(project.FolderPath, project.FolderPath, value);
                    if (path != null) paths.Add(path);
                }

                if (root.TryGetProperty("build", out var build) &&
                    build.TryGetProperty("win", out var win))
                {
                    foreach (var value in GetStringProperties(win, "icon"))
                    {
                        var path = ResolveProjectPath(project.FolderPath, project.FolderPath, value);
                        if (path != null) paths.Add(path);
                    }
                }
            }
            catch
            {
                // package.json の形式が不正でも、他の候補の探索は続けます。
            }

            foreach (var path in paths) yield return path;
        }
    }

    private static IEnumerable<string> GetStringProperties(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) yield break;

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
        else if (property.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    yield return item.GetString()!;
            }
        }
    }

    private static string? ResolveProjectPath(string projectRoot, string? baseDirectory, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("$(", StringComparison.Ordinal))
            return null;

        var path = Path.IsPathRooted(value)
            ? value
            : Path.Combine(baseDirectory ?? projectRoot, value);

        try
        {
            path = Path.GetFullPath(path);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetProjectExecutables(string projectRoot)
    {
        return EnumerateFiles(projectRoot, "*.exe")
            .Where(path => !IsIgnoredPath(path))
            .OrderByDescending(GetExecutablePriority)
            .ThenByDescending(GetLastWriteTimeUtc);
    }

    private static IEnumerable<string> GetKnownIconFiles(string projectRoot)
    {
        foreach (var name in KnownIconNames)
        {
            var path = Path.Combine(projectRoot, name);
            if (File.Exists(path)) yield return path;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsIgnoredPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("vendor", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetExecutablePriority(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var score = 0;
        if (parts.Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase))) score += 100;
        if (parts.Any(part => part.Equals("publish", StringComparison.OrdinalIgnoreCase))) score += 20;
        if (parts.Any(part => part.Equals("dist", StringComparison.OrdinalIgnoreCase))) score += 10;
        return score - parts.Length;
    }

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static ImageSource? LoadImageFile(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.DecodePixelHeight = 32;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadExecutableIcon(string path)
    {
        try
        {
            var fileInfo = new ShFileInfo();
            var result = SHGetFileInfo(path, 0, ref fileInfo, (uint)Marshal.SizeOf<ShFileInfo>(),
                                       ShgfiIcon | ShgfiLargeIcon);
            if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero) return null;

            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(
                    fileInfo.IconHandle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                image.Freeze();
                return image;
            }
            finally
            {
                DestroyIcon(fileInfo.IconHandle);
            }
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint fileAttributes, ref ShFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
