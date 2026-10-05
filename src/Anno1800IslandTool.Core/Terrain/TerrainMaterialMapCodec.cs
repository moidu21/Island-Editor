namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Codec haut niveau pour MaterialIDs2 / MaterialAlphas2.
/// 
/// MaterialIDs2 est encodé par bloc MaterialIdBlockSize x MaterialIdBlockSize.
/// MaterialAlphas2 est encodé par texel. Les deux utilisent le packing 6 slots x 5 bits
/// décrit par les shaders terrain.
/// </summary>
public static class TerrainMaterialMapCodec
{
    public static TerrainMaterialCell[] DecodeCells(TerrainMaterialMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Width <= 0 || map.Height <= 0)
        {
            return Array.Empty<TerrainMaterialCell>();
        }

        var cells = new TerrainMaterialCell[map.Width * map.Height];
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                cells[(y * map.Width) + x] = map.GetCell(x, y);
            }
        }

        return cells;
    }

    public static TerrainMaterialMap EncodeFromCells(
        int width,
        int height,
        int materialIdBlockSize,
        IReadOnlyList<TerrainMaterialCell> cells)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (materialIdBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(materialIdBlockSize));
        }

        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count < width * height)
        {
            throw new ArgumentException($"Il faut au moins {width * height} cellules pour encoder {width}x{height}.", nameof(cells));
        }

        var map = new TerrainMaterialMap
        {
            Width = width,
            Height = height,
            MaterialIdBlockSize = materialIdBlockSize
        };

        // Les MaterialIDs2 sont partagés par bloc. On encode d'abord les IDs de la première
        // cellule rencontrée pour chaque bloc, puis les alphas par texel.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = cells[(y * width) + x];
                var blockX = x / materialIdBlockSize;
                var blockY = y / materialIdBlockSize;
                var blockIndex = blockY * map.BlockWidth + blockX;

                if (map.PackedMaterialIds.Length == 0)
                {
                    map.PackedMaterialIds = new uint[map.ExpectedPackedMaterialIdCount];
                }

                if (map.PackedMaterialIds[blockIndex] == 0)
                {
                    map.SetMaterialIdBlock(blockX, blockY, cell.MaterialIds);
                }

                map.SetCellAlphaWeights(x, y, cell.AlphaWeights);
            }
        }

        return map;
    }

    public static void EnsureExpectedArraySizes(TerrainMaterialMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.ExpectedPackedMaterialIdCount > 0 && map.PackedMaterialIds.Length != map.ExpectedPackedMaterialIdCount)
        {
            var ids = map.PackedMaterialIds;
            Array.Resize(ref ids, map.ExpectedPackedMaterialIdCount);
            map.PackedMaterialIds = ids;
        }

        if (map.ExpectedPackedAlphaCount > 0 && map.PackedMaterialAlphas.Length != map.ExpectedPackedAlphaCount)
        {
            var alphas = map.PackedMaterialAlphas;
            Array.Resize(ref alphas, map.ExpectedPackedAlphaCount);
            map.PackedMaterialAlphas = alphas;
        }
    }

    public static bool HasExpectedArraySizes(TerrainMaterialMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.PackedMaterialIds.Length == map.ExpectedPackedMaterialIdCount &&
               map.PackedMaterialAlphas.Length == map.ExpectedPackedAlphaCount;
    }
}
