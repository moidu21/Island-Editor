using Anno1800IslandTool.Core.Common;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Pinceau MaterialIDs2 / MaterialAlphas2. Les MaterialIDs2 étant partagés par bloc,
/// le painter évite de remplacer un slot utilisé dans d'autres texels du bloc tant qu'un
/// slot vide ou inutilisé est disponible. Le mode StampPickedCell permet de repeindre
/// une empreinte complète capturée avec pickup : IDs de slots + alphas.
/// </summary>
public static class TerrainMaterialPainter
{
    public static TerrainMaterialPaintResult PaintBrush(TerrainMaterialMap map, int centerX, int centerY, TerrainMaterialBrushOptions options)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        if (map.Width <= 0 || map.Height <= 0)
        {
            return new TerrainMaterialPaintResult();
        }

        if (options.Mode == TerrainMaterialBrushMode.StampPickedCell && options.CellPattern is null)
        {
            var missingPattern = new TerrainMaterialPaintResult();
            missingPattern.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucune cellule pickup n'est disponible pour le mode StampPickedCell."));
            return missingPattern;
        }

        var result = new TerrainMaterialPaintResult();
        var radius = options.ClampedRadius;
        var strength = Math.Max(1, options.ClampedStrength);
        var materialId = TerrainMaterialPackEncoding.ClampSlot(options.MaterialId);
        var minX = Math.Max(0, centerX - radius);
        var maxX = Math.Min(map.Width - 1, centerX + radius);
        var minY = Math.Max(0, centerY - radius);
        var maxY = Math.Min(map.Height - 1, centerY + radius);

        var originalAlphas = options.Mode == TerrainMaterialBrushMode.SmoothAlphas
            ? map.PackedMaterialAlphas.ToArray()
            : Array.Empty<uint>();

        var changedBlockIndexes = new HashSet<int>();
        var changedAny = false;
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance > radius)
                {
                    continue;
                }

                if (options.CellPredicate is not null && !options.CellPredicate(x, y))
                {
                    continue;
                }

                result.CellsVisited++;
                var influence = options.UseFalloff
                    ? Math.Max(0.05, 1.0 - (distance / Math.Max(1, radius)))
                    : 1.0;
                var amount = Math.Max(1, TerrainMaterialPackEncoding.ClampSlot((int)Math.Round(strength * influence)));

                var beforeBlockIndex = map.GetMaterialIdBlockIndex(x, y);
                var beforeBlock = map.PackedMaterialIds.Length > beforeBlockIndex ? map.PackedMaterialIds[beforeBlockIndex] : 0u;
                var beforeAlphaIndex = map.GetAlphaIndex(x, y);
                var beforeAlpha = map.PackedMaterialAlphas.Length > beforeAlphaIndex ? map.PackedMaterialAlphas[beforeAlphaIndex] : 0u;

                switch (options.Mode)
                {
                    case TerrainMaterialBrushMode.ReplaceDominant:
                        map.SetDominantMaterialSafe(x, y, materialId, TerrainMaterialPackEncoding.MaxPackedSlotValue);
                        break;

                    case TerrainMaterialBrushMode.Blend:
                        map.BlendMaterial(x, y, materialId, amount);
                        break;

                    case TerrainMaterialBrushMode.Erase:
                        map.EraseMaterial(x, y, materialId, amount);
                        break;

                    case TerrainMaterialBrushMode.SmoothAlphas:
                        SmoothCellFromOriginal(map, originalAlphas, x, y, amount);
                        break;

                    case TerrainMaterialBrushMode.StampPickedCell:
                        map.StampCellPattern(x, y, options.CellPattern!, amount);
                        break;
                }

                // v43.7 : même le mode Erase/Smooth ne doit jamais laisser une cellule active
                // sans aucun coverage MaterialAlphas2, sinon le shader terrain peut afficher
                // un fallback blanc/gris.
                if (!map.GetCellAlphaWeights(x, y).Any(a => a > 0))
                {
                    TerrainMaterialMapCoverageSanitizer.EnsureCellCoverage(map, x, y, materialId);
                }

                var afterBlock = map.PackedMaterialIds.Length > beforeBlockIndex ? map.PackedMaterialIds[beforeBlockIndex] : 0u;
                var afterAlpha = map.PackedMaterialAlphas.Length > beforeAlphaIndex ? map.PackedMaterialAlphas[beforeAlphaIndex] : 0u;
                if (beforeAlpha != afterAlpha || beforeBlock != afterBlock)
                {
                    result.CellsChanged++;
                    changedAny = true;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);

                    if (options.Mode == TerrainMaterialBrushMode.StampPickedCell && options.CellPattern is not null)
                    {
                        foreach (var id in options.CellPattern.ActiveMaterialIds)
                        {
                            result.AffectedMaterialIds.Add(id);
                        }
                    }
                    else
                    {
                        result.AffectedMaterialIds.Add(materialId);
                    }

                    var oldIds = TerrainMaterialPackEncoding.DecodeMaterialIds(beforeBlock);
                    var newIds = TerrainMaterialPackEncoding.DecodeMaterialIds(afterBlock);
                    foreach (var id in oldIds.Concat(newIds).Where(id => id > 0))
                    {
                        result.AffectedMaterialIds.Add(id);
                    }

                    if (beforeBlock != afterBlock)
                    {
                        changedBlockIndexes.Add(beforeBlockIndex);
                    }
                }
            }
        }

        result.BlocksChanged = changedBlockIndexes.Count;
        if (changedAny)
        {
            result.AffectedBounds = new RectI(left, top, right, bottom);
        }

        return result;
    }

    private static void SmoothCellFromOriginal(TerrainMaterialMap map, IReadOnlyList<uint> originalAlphas, int x, int y, int amount)
    {
        if (originalAlphas.Count == 0)
        {
            return;
        }

        var blockX = x / map.MaterialIdBlockSize;
        var blockY = y / map.MaterialIdBlockSize;
        var blockStartX = blockX * map.MaterialIdBlockSize;
        var blockStartY = blockY * map.MaterialIdBlockSize;
        var blockEndX = Math.Min(map.Width - 1, blockStartX + map.MaterialIdBlockSize - 1);
        var blockEndY = Math.Min(map.Height - 1, blockStartY + map.MaterialIdBlockSize - 1);

        var sum = new int[TerrainMaterialPackEncoding.SlotCount];
        var count = 0;
        for (var yy = blockStartY; yy <= blockEndY; yy++)
        {
            for (var xx = blockStartX; xx <= blockEndX; xx++)
            {
                var index = map.GetAlphaIndex(xx, yy);
                if (index < 0 || index >= originalAlphas.Count)
                {
                    continue;
                }

                var weights = TerrainMaterialPackEncoding.DecodeMaterialAlphas(originalAlphas[index]);
                for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
                {
                    sum[i] += weights[i];
                }
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        var current = map.GetCellAlphaWeights(x, y);
        var smoothed = new int[TerrainMaterialPackEncoding.SlotCount];
        var blend = amount / (double)TerrainMaterialPackEncoding.MaxPackedSlotValue;
        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            var avg = (int)Math.Round(sum[i] / (double)count);
            smoothed[i] = TerrainMaterialPackEncoding.ClampSlot((int)Math.Round(current[i] * (1.0 - blend) + avg * blend));
        }

        // v30 : ne jamais ramener la somme des MaterialAlphas2 à 31.
        // Les slots sont des coverages indépendants dans les shaders terrain vanilla.
        map.SetCellAlphaWeights(x, y, TerrainMaterialPackEncoding.ClampAlphaWeightsPreservingCoverage(smoothed));
    }
}
