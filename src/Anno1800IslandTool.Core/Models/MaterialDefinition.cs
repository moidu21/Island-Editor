namespace Anno1800IslandTool.Core.Models;

public sealed class MaterialDefinition
{
    public int Id { get; set; }
    public string? Type { get; set; }
    public string? FileName { get; set; }
    public float? Repetition { get; set; }
    public float? AlphaSharpness { get; set; }
    public string? GroundType { get; set; }
    public string? ObjectFileName { get; set; }
    public Dictionary<string, string> RawFields { get; set; } = new();
}
