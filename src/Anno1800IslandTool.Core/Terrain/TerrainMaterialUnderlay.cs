namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Add a lower opaque layer without evicting any neighbour's paint.</summary>
public static class TerrainMaterialUnderlay
{
    public static bool EnsureCell(TerrainMaterialMap map, int x, int y, int baseId)
    {
        var bx = x / map.MaterialIdBlockSize;
        var by = y / map.MaterialIdBlockSize;
        var ids = map.GetMaterialIdBlock(bx, by);
        var slot = Array.IndexOf(ids, baseId);
        if (slot < 0)
        {
            slot = Array.IndexOf(map.GetBlockAlphaUsage(bx, by), 0);
            if (slot < 0) return false;
            ids[slot] = baseId;
            map.SetMaterialIdBlock(bx, by, ids);
        }
        var weights = map.GetCellAlphaWeights(x, y);
        if (weights[slot] == 31) return false;
        weights[slot] = 31;
        map.SetCellAlphaWeights(x, y, weights);
        return true;
    }
}
