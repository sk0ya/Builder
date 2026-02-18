using System.IO;
using System.Text.Json;
using Builder.Models;

namespace Builder.Services;

public class AppSettings
{
    public List<ProjectEntry> Projects { get; set; } = [];
    public string BackgroundColor { get; set; } = "#1E1E1E";
    public string AccentColor { get; set; } = "#4FC3F7";
    public string LastCloneParentFolder { get; set; } = string.Empty;
}

public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Builder");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new AppSettings();

        var json = File.ReadAllText(SettingsFile);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFile, json);
    }
}
