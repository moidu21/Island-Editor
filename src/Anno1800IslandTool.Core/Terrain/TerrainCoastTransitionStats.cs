namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainCoastTransitionStats
{
    public int DeepWaterCells { get; set; }
    public int ShallowWaterCells { get; set; }
    public int BeachCells { get; set; }
    public int LowLandCells { get; set; }
    public int RockCells { get; set; }
    public int SkippedCells { get; set; }

    public int PaintedCells => DeepWaterCells + ShallowWaterCells + BeachCells + LowLandCells + RockCells;
}
