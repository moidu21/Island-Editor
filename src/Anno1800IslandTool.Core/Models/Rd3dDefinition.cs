using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Core.Models;

public sealed class Rd3dDefinition
{
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }
    public int MaterialMapWidth { get; set; }
    public int MaterialMapHeight { get; set; }
    public int MaterialIDBlockSize { get; set; } = 2;
    public TerrainMaterialMap MaterialMap { get; set; } = new();
    public int TintMapWidth { get; set; }
    public int TintMapHeight { get; set; }
    public int NodeSize { get; set; }
    public float UnitScale { get; set; }
    public int PageSize { get; set; }
    public int TintPageSize { get; set; }
    public int MinMeshLevel { get; set; }
    public int NodesPerMeshCluster { get; set; }
    public bool UsingSplitImageHeightMaps { get; set; }
    public int HeightPageBorderTexels { get; set; }
    public string? MaterialSetFileName { get; set; }
    public PropGridDefinition PropGrid { get; set; } = new();
    public List<string> RawLevels { get; set; } = new();
    public Dictionary<string, string> RawUnknownBlocks { get; set; } = new();
}
