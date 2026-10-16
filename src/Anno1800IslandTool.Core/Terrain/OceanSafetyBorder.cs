namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Only the actual file extent bounds editing; no reserved ocean margin.</summary>
public static class OceanSafetyBorder
{
    public const double Width = 0;
    public static bool Contains(double x, double z, double worldWidth, double worldHeight)
        => x < 0 || z < 0 || x > worldWidth || z > worldHeight;
    public static bool IntersectsCell(int x, int z, int columns, int rows, double worldWidth, double worldHeight)
        => x < 0 || z < 0 || x >= columns || z >= rows;
}
