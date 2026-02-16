namespace Builder.Models;

public class ProjectAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
}
