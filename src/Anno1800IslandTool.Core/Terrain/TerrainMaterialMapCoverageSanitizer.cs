namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Sécurise la MaterialMap contre les texels sans aucun coverage MaterialAlphas2.
/// Le shader terrain clippe les splats avec coverage ; un texel dont les 6 alphas valent 0
/// peut produire un fallback blanc/gris, surtout après un remap de MaterialIDs2 partagé par bloc 2x2.
/// </summary>
public static class TerrainMaterialMapCoverageSanitizer
{
    public const int DefaultFallbackMaterialId = 10;

    public static int EnsureNoZeroCoverageTexels(
        TerrainMaterialMap map,
        Func<int, int, bool>? predicate = null,
        int fallbackMaterialId = DefaultFallbackMaterialId)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Width <= 0 || map.Height <= 0 || map.PackedMaterialAlphas.Length == 0)
        {
            return 0;
        }

        var fixedCells = 0;
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (predicate is not null && !predicate(x, y))
                {
                    continue;
                }

                var alphaIndex = map.GetAlphaIndex(x, y);
                var alphas = TerrainMaterialPackEncoding.DecodeMaterialAlphas(map.PackedMaterialAlphas[alphaIndex]);
                if (alphas.Any(a => a > 0))
                {
                    continue;
                }

                EnsureCellCoverage(map, x, y, fallbackMaterialId);
                fixedCells++;
            }
        }

        return fixedCells;
    }

    public static void EnsureCellCoverage(TerrainMaterialMap map, int x, int y, int fallbackMaterialId = DefaultFallbackMaterialId)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Width <= 0 || map.Height <= 0)
        {
            return;
        }

        var blockIndex = map.GetMaterialIdBlockIndex(x, y);
        if (map.PackedMaterialIds.Length == 0)
        {
            TerrainMaterialMapCodec.EnsureExpectedArraySizes(map);
        }

        var ids = TerrainMaterialPackEncoding.DecodeMaterialIds(map.PackedMaterialIds[blockIndex]);
        var slot = FindBestFallbackSlot(ids);
        if (slot < 0)
        {
            slot = 0;
            ids[slot] = TerrainMaterialPackEncoding.ClampSlot(fallbackMaterialId);
            map.PackedMaterialIds[blockIndex] = TerrainMaterialPackEncoding.EncodeMaterialIds(ids);
        }

        var alpha = new int[TerrainMaterialPackEncoding.SlotCount];
        alpha[slot] = TerrainMaterialPackEncoding.MaxPackedSlotValue;
        map.SetCellAlphaWeights(x, y, alpha);
    }

    private static int FindBestFallbackSlot(IReadOnlyList<int> ids)
    {
        for (var i = 0; i < ids.Count && i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            var id = TerrainMaterialPackEncoding.ClampSlot(ids[i]);
            if (id > 0 && id != TerrainMaterialPackEncoding.MaxPackedSlotValue)
            {
                return i;
            }
        }

        for (var i = 0; i < ids.Count && i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            if (TerrainMaterialPackEncoding.ClampSlot(ids[i]) > 0)
            {
                return i;
            }
        }

        return -1;
    }
}
