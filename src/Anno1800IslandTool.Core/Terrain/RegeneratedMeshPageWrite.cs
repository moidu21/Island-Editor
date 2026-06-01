namespace Anno1800IslandTool.Core.Terrain;

public sealed class RegeneratedMeshPageWrite
{
    public TerrainPageKey Key { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string XmlText { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public MeshPageComparison? Comparison { get; set; }

    public bool HasContent => !string.IsNullOrWhiteSpace(TargetPath) && !string.IsNullOrWhiteSpace(XmlText);
}
