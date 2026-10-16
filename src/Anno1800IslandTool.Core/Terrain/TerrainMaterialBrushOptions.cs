namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainMaterialBrushOptions
{
    public int MaterialId { get; set; }
    public int Radius { get; set; } = 6;
    public int Strength { get; set; } = TerrainMaterialPackEncoding.MaxPackedSlotValue;
    public TerrainMaterialBrushMode Mode { get; set; } = TerrainMaterialBrushMode.Blend;
    public bool UseFalloff { get; set; } = true;
    public TerrainMaterialCellPattern? CellPattern { get; set; }
    public Func<int, int, bool>? CellPredicate { get; set; }

    public int ClampedRadius => Math.Clamp(Radius, 1, 512);
    public int ClampedStrength => TerrainMaterialPackEncoding.ClampSlot(Strength);
}
