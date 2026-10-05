using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Bounds spatiaux normalisés d'une page terrain, projetés sur la coarse heightmap GameData.
///
/// Les familles Mesh / NormalMap / TintMap ne partagent pas toujours les mêmes clés absolues
/// (LOD/X/Y). Cette structure permet donc de raisonner par recouvrement de zones plutôt que
/// par égalité stricte de clé.
/// </summary>
public sealed class TerrainPageBounds
{
    public string Family { get; set; } = string.Empty;
    public TerrainPageKey Key { get; set; }
    public RectI SampleBounds { get; set; }
    public int GridMinX { get; set; }
    public int GridMinY { get; set; }
    public int GridMaxX { get; set; }
    public int GridMaxY { get; set; }
    public int GridColumns { get; set; }
    public int GridRows { get; set; }
    public int ColumnIndex { get; set; }
    public int RowIndex { get; set; }
    public bool IsExactKeyGrid { get; set; }
    public string SourcePath { get; set; } = string.Empty;

    public int Width => Math.Max(0, SampleBounds.Right - SampleBounds.Left + 1);
    public int Height => Math.Max(0, SampleBounds.Bottom - SampleBounds.Top + 1);

    public bool Intersects(RectI other)
        => TerrainPageCoverage.IntersectsInclusive(SampleBounds, other);

    public override string ToString()
        => $"{Family} {Key} samples={SampleBounds} grid={GridColumns}x{GridRows}";
}
