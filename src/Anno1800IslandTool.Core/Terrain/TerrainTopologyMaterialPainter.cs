using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Performance;

namespace Anno1800IslandTool.Core.Terrain;

public enum TerrainTopologyRole
{
    None,
    Flat,
    GentleSlope,
    SteepSlope,
    Ridge,
    Hollow
}

/// <summary>
/// Peint la material map à partir de la pente et de la courbure locales du relief.
/// La hauteur absolue n'est jamais utilisée pour choisir une classe.
/// </summary>
public static class TerrainTopologyMaterialPainter
{
    private readonly record struct TopologyDecision(TerrainTopologyRole Role, int MaterialId);

    public static TerrainMaterialPaintResult Apply(
        TerrainMaterialMap materialMap,
        CoarseHeightMap heightMap,
        TerrainTopologyMaterialOptions options,
        out TerrainTopologyMaterialStats stats,
        RectI? materialCellBounds = null,
        Func<int, int, bool>? activeMaterialCellPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(materialMap);
        ArgumentNullException.ThrowIfNull(heightMap);
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();
        stats = new TerrainTopologyMaterialStats();
        var result = new TerrainMaterialPaintResult();
        if (materialMap.Width <= 0 || materialMap.Height <= 0 || heightMap.Width <= 1 || heightMap.Height <= 1)
        {
            result.Messages.Add("Material map ou heightmap vide : peinture topologique annulée.");
            return result;
        }

        var bounds = NormalizeBounds(materialMap, materialCellBounds);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            result.Messages.Add("Peinture topologique ignorée : zone vide.");
            return result;
        }

        var decisions = new TopologyDecision[materialMap.Width * materialMap.Height];
        TerrainComputeScheduler.For(bounds.Top, bounds.Bottom, 64, y =>
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var index = (y * materialMap.Width) + x;
                if (activeMaterialCellPredicate is not null && !activeMaterialCellPredicate(x, y))
                {
                    decisions[index] = new TopologyDecision(TerrainTopologyRole.None, 0);
                    continue;
                }

                if (options.PreserveExistingWaterAndBeach &&
                    options.PreservedMaterialIds.Contains(materialMap.GetDominantMaterialId(x, y)))
                {
                    decisions[index] = new TopologyDecision(TerrainTopologyRole.None, -1);
                    continue;
                }

                var metrics = AnalyzeCell(heightMap, materialMap, x, y, options.AnalysisRadius);
                var role = Classify(metrics.Slope, metrics.Curvature, options);
                decisions[index] = new TopologyDecision(role, ResolveMaterialId(role, options));
            }
        });

        var changedBlocks = new HashSet<int>();
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                result.CellsVisited++;
                var decision = decisions[(y * materialMap.Width) + x];
                if (decision.Role == TerrainTopologyRole.None)
                {
                    if (decision.MaterialId < 0)
                    {
                        stats.PreservedCells++;
                    }
                    else
                    {
                        stats.SkippedCells++;
                    }
                    continue;
                }

                AddRoleStats(stats, decision.Role);
                var materialId = TerrainMaterialPackEncoding.ClampSlot(decision.MaterialId);
                var blockIndex = materialMap.GetMaterialIdBlockIndex(x, y);
                var alphaIndex = materialMap.GetAlphaIndex(x, y);
                var beforeBlock = materialMap.PackedMaterialIds[blockIndex];
                var beforeAlpha = materialMap.PackedMaterialAlphas[alphaIndex];

                if (options.UseBlend && options.ClampedStrength < TerrainMaterialPackEncoding.MaxPackedSlotValue)
                {
                    materialMap.BlendMaterial(x, y, materialId, options.ClampedStrength);
                }
                else
                {
                    materialMap.SetDominantMaterialSafe(x, y, materialId, options.ClampedStrength);
                }

                if (decision.Role == TerrainTopologyRole.Flat && options.FlatTilingEnabled
                    && options.FlatTilingMaterialId != materialId)
                {
                    // Keep an opaque base, as in native multi-layer material cells.
                    // Never evict a neighbour's material merely to add a decorative layer.
                    var ids = materialMap.GetMaterialIdBlock(x/materialMap.MaterialIdBlockSize,y/materialMap.MaterialIdBlockSize);
                    int slot = Array.IndexOf(ids, options.FlatTilingMaterialId);
                    if (slot < 0)
                    {
                        slot = Array.IndexOf(materialMap.GetBlockAlphaUsage(x/materialMap.MaterialIdBlockSize,y/materialMap.MaterialIdBlockSize),0);
                        if (slot >= 0)
                        {
                            ids[slot] = options.FlatTilingMaterialId;
                            materialMap.SetMaterialIdBlock(x/materialMap.MaterialIdBlockSize,y/materialMap.MaterialIdBlockSize,ids);
                        }
                    }
                    if (slot >= 0)
                    {
                        double wave = Math.Cos((x+.5)*Math.PI/options.FlatTileSize)*Math.Cos((y+.5)*Math.PI/options.FlatTileSize);
                        var weights = materialMap.GetCellAlphaWeights(x,y);
                        int baseSlot = Array.IndexOf(ids,materialId);
                        int accent=(int)Math.Round(options.FlatTileContrast*(.5+.5*wave));
                        if(baseSlot>=0) weights[baseSlot]=options.FlatTilingOverlayAboveBase?31:31-accent;
                        weights[slot]=options.FlatTilingOverlayAboveBase?accent:31;
                        materialMap.SetCellAlphaWeights(x,y,weights);
                        result.AffectedMaterialIds.Add(options.FlatTilingMaterialId);
                    }
                }
                var afterBlock = materialMap.PackedMaterialIds[blockIndex];
                var afterAlpha = materialMap.PackedMaterialAlphas[alphaIndex];
                if (beforeBlock == afterBlock && beforeAlpha == afterAlpha)
                {
                    continue;
                }

                result.CellsChanged++;
                result.AffectedMaterialIds.Add(materialId);
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
                if (beforeBlock != afterBlock)
                {
                    changedBlocks.Add(blockIndex);
                }
            }
        }

        result.BlocksChanged = changedBlocks.Count;
        if (result.CellsChanged > 0)
        {
            result.AffectedBounds = new RectI(left, top, right, bottom);
        }

        result.Messages.Add($"Peinture topologique : plats={stats.FlatCells}, pentes douces={stats.GentleSlopeCells}, pentes fortes={stats.SteepSlopeCells}, crêtes={stats.RidgeCells}, creux={stats.HollowCells}, préservées={stats.PreservedCells}, ignorées={stats.SkippedCells}. Analyse locale rayon={options.AnalysisRadius}, sans seuil d'altitude.");
        return result;
    }

    public static (float Slope, float Curvature) AnalyzeCell(
        CoarseHeightMap heightMap,
        TerrainMaterialMap materialMap,
        int x,
        int y,
        int analysisRadius)
    {
        var hx = ((x + 0.5) / Math.Max(1.0, materialMap.Width)) * (heightMap.Width - 1);
        var hy = ((y + 0.5) / Math.Max(1.0, materialMap.Height)) * (heightMap.Height - 1);
        var stepX = Math.Max(1.0, (heightMap.Width - 1.0) / Math.Max(1, materialMap.Width)) * Math.Clamp(analysisRadius, 1, 16);
        var stepY = Math.Max(1.0, (heightMap.Height - 1.0) / Math.Max(1, materialMap.Height)) * Math.Clamp(analysisRadius, 1, 16);

        var center = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy);
        var left = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx - stepX, hy);
        var right = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx + stepX, hy);
        var top = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy - stepY);
        var bottom = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy + stepY);

        var dx = (right - left) / (2f * (float)stepX);
        var dy = (bottom - top) / (2f * (float)stepY);
        var slope = MathF.Sqrt((dx * dx) + (dy * dy));
        var curvature = center - ((left + right + top + bottom) * 0.25f);
        return (slope, curvature);
    }

    public static TerrainTopologyRole Classify(float slope, float curvature, TerrainTopologyMaterialOptions options)
    {
        options.Normalize();
        if (curvature >= options.CurvatureSensitivity)
        {
            return TerrainTopologyRole.Ridge;
        }
        if (curvature <= -options.CurvatureSensitivity)
        {
            return TerrainTopologyRole.Hollow;
        }
        if (slope >= options.SteepSlopeMinimum)
        {
            return TerrainTopologyRole.SteepSlope;
        }
        return slope <= options.FlatSlopeMaximum ? TerrainTopologyRole.Flat : TerrainTopologyRole.GentleSlope;
    }

    private static int ResolveMaterialId(TerrainTopologyRole role, TerrainTopologyMaterialOptions options)
        => role switch
        {
            TerrainTopologyRole.Flat => options.FlatMaterialId,
            TerrainTopologyRole.GentleSlope => options.GentleSlopeMaterialId,
            TerrainTopologyRole.SteepSlope => options.SteepSlopeMaterialId,
            TerrainTopologyRole.Ridge => options.RidgeMaterialId,
            TerrainTopologyRole.Hollow => options.HollowMaterialId,
            _ => 0
        };

    private static void AddRoleStats(TerrainTopologyMaterialStats stats, TerrainTopologyRole role)
    {
        switch (role)
        {
            case TerrainTopologyRole.Flat: stats.FlatCells++; break;
            case TerrainTopologyRole.GentleSlope: stats.GentleSlopeCells++; break;
            case TerrainTopologyRole.SteepSlope: stats.SteepSlopeCells++; break;
            case TerrainTopologyRole.Ridge: stats.RidgeCells++; break;
            case TerrainTopologyRole.Hollow: stats.HollowCells++; break;
        }
    }

    private static RectI NormalizeBounds(TerrainMaterialMap map, RectI? requested)
    {
        if (!requested.HasValue)
        {
            return new RectI(0, 0, map.Width, map.Height);
        }
        var b = requested.Value;
        return new RectI(
            Math.Clamp(Math.Min(b.Left, b.Right), 0, map.Width),
            Math.Clamp(Math.Min(b.Top, b.Bottom), 0, map.Height),
            Math.Clamp(Math.Max(b.Left, b.Right), 0, map.Width),
            Math.Clamp(Math.Max(b.Top, b.Bottom), 0, map.Height));
    }
}
