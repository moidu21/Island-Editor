namespace Anno1800IslandTool.Core.Terrain;

public sealed class MeshCluster
{
    public List<MeshVertex> Vertices { get; set; } = new();
    public List<int> Indices { get; set; } = new();
    public int UsedMaterialBits { get; set; }
    public List<GroundObjectInstance> GroundObjects { get; set; } = new();
}
