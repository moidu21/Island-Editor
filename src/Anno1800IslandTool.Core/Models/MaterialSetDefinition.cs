namespace Anno1800IslandTool.Core.Models;

public sealed class MaterialSetDefinition
{
    public string FileName { get; set; } = string.Empty;
    public List<MaterialDefinition> Materials { get; set; } = new();
}
