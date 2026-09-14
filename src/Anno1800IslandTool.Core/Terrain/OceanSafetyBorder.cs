namespace Anno1800IslandTool.Core.Terrain;

/// <summary>The reserved ocean border is independent of the editable footprint.</summary>
public static class OceanSafetyBorder
{
    public const double Width = 256;
    public static bool Contains(double x, double z, double worldWidth, double worldHeight)
        => x <= Width || z <= Width || x >= worldWidth - Width || z >= worldHeight - Width;

    public static bool IntersectsCell(int x, int z, int columns, int rows, double worldWidth, double worldHeight)
        => x * worldWidth / columns < Width || z * worldHeight / rows < Width ||
           (x + 1) * worldWidth / columns > worldWidth - Width ||
           (z + 1) * worldHeight / rows > worldHeight - Width;
}
