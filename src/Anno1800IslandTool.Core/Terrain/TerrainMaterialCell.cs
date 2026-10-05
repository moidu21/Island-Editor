namespace Anno1800IslandTool.Core.Terrain;

public sealed class TerrainMaterialCell
{
    public int X { get; init; }
    public int Y { get; init; }
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
}
