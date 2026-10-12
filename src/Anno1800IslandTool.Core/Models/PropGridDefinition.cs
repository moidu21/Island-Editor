namespace Anno1800IslandTool.Core.Models;

public sealed class PropGridDefinition
{
    public List<string> FileNames { get; set; } = new();
    public List<PropGridInstanceDefinition> Instances { get; set; } = new();
}
