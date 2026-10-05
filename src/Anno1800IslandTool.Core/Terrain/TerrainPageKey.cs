namespace Anno1800IslandTool.Core.Terrain;

public readonly record struct TerrainPageKey(int Lod, int X, int Y)
{
    public override string ToString() => $"LOD{Lod}:{X}x{Y}";
}
