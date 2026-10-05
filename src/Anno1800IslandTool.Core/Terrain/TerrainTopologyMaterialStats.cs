namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainTopologyMaterialStats
{
    public int FlatCells { get; set; }
    public int GentleSlopeCells { get; set; }
    public int SteepSlopeCells { get; set; }
    public int RidgeCells { get; set; }
    public int HollowCells { get; set; }
    public int PreservedCells { get; set; }
    public int SkippedCells { get; set; }
}
