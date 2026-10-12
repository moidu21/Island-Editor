namespace Anno1800IslandTool.Core.Terrain;

public sealed class GroundObjectPlacement
{
    public TerrainPageKey PageKey { get; set; }
    public string PagePath { get; set; } = string.Empty;
    public int ClusterIndex { get; set; }
    public GroundObjectInstance Object { get; set; } = new();

    public bool IsOnProtectedMetadataLod => PageKey.Lod <= 1;
}
