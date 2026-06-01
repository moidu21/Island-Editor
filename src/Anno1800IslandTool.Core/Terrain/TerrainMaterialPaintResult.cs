using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainMaterialPaintResult
{
    public int CellsVisited { get; set; }
    public int CellsChanged { get; set; }
    public int BlocksChanged { get; set; }
    public RectI? AffectedBounds { get; set; }
    public HashSet<int> AffectedMaterialIds { get; } = new();
    public List<string> Messages { get; } = new();

    public bool HasChanges => CellsChanged > 0 || BlocksChanged > 0;

    public void Merge(TerrainMaterialPaintResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        CellsVisited += other.CellsVisited;
        CellsChanged += other.CellsChanged;
        BlocksChanged += other.BlocksChanged;
        foreach (var id in other.AffectedMaterialIds)
        {
            AffectedMaterialIds.Add(id);
        }
        Messages.AddRange(other.Messages);

        if (other.AffectedBounds is null)
        {
            return;
        }

        if (AffectedBounds is null)
        {
            AffectedBounds = other.AffectedBounds;
            return;
        }

        var a = AffectedBounds.Value;
        var b = other.AffectedBounds.Value;
        AffectedBounds = new RectI(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));
    }
}
