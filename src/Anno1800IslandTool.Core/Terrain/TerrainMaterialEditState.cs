using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainMaterialEditState
{
    public int TotalPaintOperations { get; set; }
    public int TotalCellsChanged { get; set; }
    public int TotalBlocksChanged { get; set; }
    public RectI? AffectedBounds { get; set; }
    public HashSet<int> MaterialIdsTouched { get; } = new();
    public List<string> RecentOperations { get; } = new();

    public bool HasEdits => TotalPaintOperations > 0 && (TotalCellsChanged > 0 || TotalBlocksChanged > 0);

    public void Add(TerrainMaterialPaintResult result, TerrainMaterialBrushOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        if (!result.HasChanges)
        {
            return;
        }

        TotalPaintOperations++;
        TotalCellsChanged += result.CellsChanged;
        TotalBlocksChanged += result.BlocksChanged;
        foreach (var id in result.AffectedMaterialIds)
        {
            MaterialIdsTouched.Add(id);
        }

        if (result.AffectedBounds is not null)
        {
            if (AffectedBounds is null)
            {
                AffectedBounds = result.AffectedBounds;
            }
            else
            {
                var a = AffectedBounds.Value;
                var b = result.AffectedBounds.Value;
                AffectedBounds = new RectI(
                    Math.Min(a.Left, b.Left),
                    Math.Min(a.Top, b.Top),
                    Math.Max(a.Right, b.Right),
                    Math.Max(a.Bottom, b.Bottom));
            }
        }

        var idText = options.Mode == TerrainMaterialBrushMode.StampPickedCell && options.CellPattern is not null
            ? $"pickup={string.Join(",", options.CellPattern.ActiveMaterialIds)}"
            : $"id={options.MaterialId}";
        RecentOperations.Add($"{options.Mode} {idText} rayon={options.ClampedRadius} force={options.ClampedStrength} cellules={result.CellsChanged} blocs={result.BlocksChanged}");
        while (RecentOperations.Count > 50)
        {
            RecentOperations.RemoveAt(0);
        }
    }

    public void AddGeneratedOperation(TerrainMaterialPaintResult result, string operationLabel)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasChanges)
        {
            return;
        }

        TotalPaintOperations++;
        TotalCellsChanged += result.CellsChanged;
        TotalBlocksChanged += result.BlocksChanged;
        foreach (var id in result.AffectedMaterialIds)
        {
            MaterialIdsTouched.Add(id);
        }

        if (result.AffectedBounds is not null)
        {
            if (AffectedBounds is null)
            {
                AffectedBounds = result.AffectedBounds;
            }
            else
            {
                var a = AffectedBounds.Value;
                var b = result.AffectedBounds.Value;
                AffectedBounds = new RectI(
                    Math.Min(a.Left, b.Left),
                    Math.Min(a.Top, b.Top),
                    Math.Max(a.Right, b.Right),
                    Math.Max(a.Bottom, b.Bottom));
            }
        }

        RecentOperations.Add($"{operationLabel} cellules={result.CellsChanged} blocs={result.BlocksChanged}");
        foreach (var message in result.Messages.Take(3))
        {
            RecentOperations.Add("  " + message);
        }
        while (RecentOperations.Count > 50)
        {
            RecentOperations.RemoveAt(0);
        }
    }
}
