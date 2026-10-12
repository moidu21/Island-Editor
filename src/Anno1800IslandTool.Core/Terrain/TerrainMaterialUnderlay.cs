namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Add a lower opaque layer without evicting any neighbour's paint.</summary>
public static class TerrainMaterialUnderlay
{
    public static bool EnsureCell(TerrainMaterialMap map, int x, int y, int baseId)
    {
        var bx = x / map.MaterialIdBlockSize;
        var by = y / map.MaterialIdBlockSize;
        // The usual case already has the base in its palette. Avoid two small
        // array allocations for every terrain texel during painting/export.
        if (map.PackedMaterialIds.Length == map.ExpectedPackedMaterialIdCount &&
            map.PackedMaterialAlphas.Length == map.ExpectedPackedAlphaCount)
        {
            uint packed = map.PackedMaterialIds[map.GetMaterialIdBlockIndex(x, y)];
            for (int s = 0; s < 6; s++)
            {
                if (((packed >> (s * 5)) & 31) != baseId) continue;
                int index = map.GetAlphaIndex(x, y);
                uint mask = 31u << (s * 5), before = map.PackedMaterialAlphas[index];
                if ((before & mask) == mask) return false;
                map.PackedMaterialAlphas[index] = (before | mask) & 0x3fffffffu;
                return true;
            }
        }
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
