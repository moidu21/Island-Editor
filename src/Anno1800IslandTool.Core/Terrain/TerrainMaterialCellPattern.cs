namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Empreinte de cellule MaterialIDs2 / MaterialAlphas2 capturée avec l'outil pickup.
/// Elle conserve les slots matériaux et leurs poids afin de pouvoir repeindre la même combinaison ailleurs.
/// </summary>
public sealed class TerrainMaterialCellPattern
{
    public int SourceX { get; init; }
    public int SourceY { get; init; }
    public int[] MaterialIds { get; init; } = new int[TerrainMaterialPackEncoding.SlotCount];
    public int[] AlphaWeights { get; init; } = new int[TerrainMaterialPackEncoding.SlotCount];

    public int DominantSlot
    {
        get
        {
            var bestSlot = 0;
            var bestWeight = int.MinValue;
            for (var i = 0; i < AlphaWeights.Length; i++)
            {
                if (AlphaWeights[i] > bestWeight)
                {
                    bestWeight = AlphaWeights[i];
                    bestSlot = i;
                }
            }

            return bestSlot;
        }
    }

    public int DominantMaterialId => MaterialIds.Length == 0 ? 0 : MaterialIds[Math.Clamp(DominantSlot, 0, MaterialIds.Length - 1)];
    public int DominantWeight => AlphaWeights.Length == 0 ? 0 : AlphaWeights[Math.Clamp(DominantSlot, 0, AlphaWeights.Length - 1)];

    public IEnumerable<int> ActiveMaterialIds
        => MaterialIds
            .Take(TerrainMaterialPackEncoding.SlotCount)
            .Where(id => id > 0)
            .Distinct();

    public static TerrainMaterialCellPattern FromCell(TerrainMaterialCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return new TerrainMaterialCellPattern
        {
            SourceX = cell.X,
            SourceY = cell.Y,
            MaterialIds = PadAndClamp(cell.MaterialIds),
            AlphaWeights = PadAndClamp(cell.AlphaWeights)
        };
    }

    public TerrainMaterialCellPattern Clone()
        => new()
        {
            SourceX = SourceX,
            SourceY = SourceY,
            MaterialIds = PadAndClamp(MaterialIds),
            AlphaWeights = PadAndClamp(AlphaWeights)
        };

    private static int[] PadAndClamp(IReadOnlyList<int> values)
    {
        var result = new int[TerrainMaterialPackEncoding.SlotCount];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = TerrainMaterialPackEncoding.ClampSlot(i < values.Count ? values[i] : 0);
        }

        return result;
    }
}
