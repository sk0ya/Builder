using System.Text.Json.Serialization;

namespace Builder.Models;

public class ProjectEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string BuildCommand { get; set; } = string.Empty;
    public string LaunchCommand { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsGitRepository => System.IO.Directory.Exists(System.IO.Path.Combine(FolderPath, ".git"));
}
