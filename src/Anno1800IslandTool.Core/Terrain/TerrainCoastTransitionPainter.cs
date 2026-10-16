using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Performance;

namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Génère une transition côte/plage/eau à partir de la heightmap GameData.
/// L'outil travaille sur la material map décodée/encodée et ne modifie pas la hauteur.
/// </summary>
public static class TerrainCoastTransitionPainter
{
    public const int DefaultBeachCoastBandCells = 18;
    public static TerrainMaterialPaintResult ApplyHeightToMaterialTransition(
        TerrainMaterialMap materialMap,
        CoarseHeightMap heightMap,
        TerrainCoastTransitionOptions options,
        out TerrainCoastTransitionStats stats,
        RectI? materialCellBounds = null,
        Func<int, int, bool>? activeMaterialCellPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(materialMap);
        ArgumentNullException.ThrowIfNull(heightMap);
        ArgumentNullException.ThrowIfNull(options);

        stats = new TerrainCoastTransitionStats();
        options.NormalizeThresholds();

        var result = new TerrainMaterialPaintResult();
        if (materialMap.Width <= 0 || materialMap.Height <= 0 || heightMap.Width <= 0 || heightMap.Height <= 0)
        {
            result.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Material map ou heightmap vide : transition côte/plage/eau annulée."));
            return result;
        }

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        var changedBlocks = new HashSet<int>();
        var changedAny = false;
        var bounds = NormalizeMaterialCellBounds(materialMap, materialCellBounds);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            result.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.T("Transition hauteur -> matériau ignorée : bounds matériau vides."));
            return result;
        }

        var waterDistanceMap = BuildWaterDistanceMap(heightMap, materialMap, options.WaterLineHeight);
        var decisionCount = Math.Max(0, materialMap.Width * materialMap.Height);
        var decisions = new TerrainTransitionDecision[decisionCount];

        // v40/v42 : classification hauteur/pente/eau en parallèle. L'application des changements
        // reste séquentielle car MaterialIDs2 est partagé par blocs de cellules et plusieurs
        // cellules peuvent écrire le même uint packé. La v42 accepte aussi une fenêtre bornée,
        // utilisée après validation 3D pour ne repeindre que les zones terrain modifiées/étendues.
        TerrainComputeScheduler.For(bounds.Top, bounds.Bottom, 64, y =>
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var index = (y * materialMap.Width) + x;
                if (activeMaterialCellPredicate is not null && !activeMaterialCellPredicate(x, y))
                {
                    decisions[index] = new TerrainTransitionDecision(TerrainTransitionRole.None, 0);
                    continue;
                }

                var worldHeight = SampleHeightAtMaterialCell(heightMap, materialMap, x, y);
                var slope = EstimateLocalSlope(heightMap, materialMap, x, y);
                var isNearWater = IsNearWater(waterDistanceMap, materialMap.Width, x, y, options.CoastBandCells);
                var role = ClassifyMaterialRole(worldHeight, slope, isNearWater, options);
                var materialId = role == TerrainTransitionRole.None ? 0 : ResolveMaterialIdForRole(role, options);
                decisions[index] = new TerrainTransitionDecision(role, materialId);
            }
        });

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                result.CellsVisited++;
                var decision = decisions[(y * materialMap.Width) + x];
                var role = decision.Role;
                if (role == TerrainTransitionRole.None)
                {
                    stats.SkippedCells++;
                    continue;
                }

                AddRoleStats(stats, role);
                var materialId = decision.MaterialId;
                var customCellPattern = ResolveCustomPatternForRole(role, options);
                var cellPattern = customCellPattern?.Clone()
                    ?? (options.UseCellPatternTemplates
                        ? BuildVanillaLikeTransitionPattern(role, materialId, options, x / Math.Max(1, materialMap.MaterialIdBlockSize), y / Math.Max(1, materialMap.MaterialIdBlockSize))
                        : null);

                var blockIndex = materialMap.GetMaterialIdBlockIndex(x, y);
                var alphaIndex = materialMap.GetAlphaIndex(x, y);
                var beforeBlock = materialMap.PackedMaterialIds.Length > blockIndex ? materialMap.PackedMaterialIds[blockIndex] : 0u;
                var beforeAlpha = materialMap.PackedMaterialAlphas.Length > alphaIndex ? materialMap.PackedMaterialAlphas[alphaIndex] : 0u;

                if (cellPattern is not null)
                {
                    materialMap.StampCellPattern(x, y, cellPattern, options.ClampedStrength);
                }
                else if (options.UseBlend && options.ClampedStrength < TerrainMaterialPackEncoding.MaxPackedSlotValue)
                {
                    materialMap.BlendMaterial(x, y, materialId, options.ClampedStrength);
                }
                else
                {
                    materialMap.SetDominantMaterialSafe(x, y, materialId, options.ClampedStrength);
                }

                var afterBlock = materialMap.PackedMaterialIds.Length > blockIndex ? materialMap.PackedMaterialIds[blockIndex] : 0u;
                var afterAlpha = materialMap.PackedMaterialAlphas.Length > alphaIndex ? materialMap.PackedMaterialAlphas[alphaIndex] : 0u;
                if (beforeBlock == afterBlock && beforeAlpha == afterAlpha)
                {
                    continue;
                }

                result.CellsChanged++;
                result.AffectedMaterialIds.Add(materialId);
                if (cellPattern is not null)
                {
                    foreach (var id in cellPattern.ActiveMaterialIds)
                    {
                        result.AffectedMaterialIds.Add(id);
                    }
                }
                changedAny = true;
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
        if (changedAny)
        {
            result.AffectedBounds = new RectI(left, top, right, bottom);
        }

        var boundsText = materialCellBounds.HasValue
            ? $" bounds={bounds.Left},{bounds.Top}->{bounds.Right},{bounds.Bottom}"
            : " global";
        var maskText = activeMaterialCellPredicate is null ? string.Empty : Anno1800IslandTool.Core.Localization.TextCatalog.T(", filtrée par ActiveMapGrid.bits");
        var customPatternCount = CountCustomTransitionPatterns(options);
        var modeText = customPatternCount > 0
            ? Anno1800IslandTool.Core.Localization.TextCatalog.F($"{customPatternCount} empreinte(s) personnalisée(s), fallback={(options.UseCellPatternTemplates ? "vanilla-like" : Anno1800IslandTool.Core.Localization.TextCatalog.T("ID dominant"))}")
            : (options.UseCellPatternTemplates ? Anno1800IslandTool.Core.Localization.TextCatalog.T("empreintes MaterialIDs2+MaterialAlphas2 vanilla-like sûres") : Anno1800IslandTool.Core.Localization.TextCatalog.T("ID dominant seul"));
        result.Messages.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Transition hauteur -> matériau v44.9{boundsText}{maskText} : eau profonde={stats.DeepWaterCells}, eau faible/corail={stats.ShallowWaterCells}, plage côtière={stats.BeachCells}, terre basse={stats.LowLandCells}, roche={stats.RockCells}, ignorées={stats.SkippedCells}, bande côte={options.CoastBandCells}. Mode={modeText}. Les empreintes personnalisées peuvent venir de la fenêtre matériaux/materialset chargé ; les patterns vanilla-like restent le fallback sécurisé."));
        return result;
    }

    private static RectI NormalizeMaterialCellBounds(TerrainMaterialMap materialMap, RectI? requestedBounds)
    {
        if (materialMap.Width <= 0 || materialMap.Height <= 0)
        {
            return new RectI(0, 0, 0, 0);
        }

        if (!requestedBounds.HasValue)
        {
            return new RectI(0, 0, materialMap.Width, materialMap.Height);
        }

        var requested = requestedBounds.Value;
        var left = Math.Clamp(Math.Min(requested.Left, requested.Right), 0, materialMap.Width);
        var top = Math.Clamp(Math.Min(requested.Top, requested.Bottom), 0, materialMap.Height);
        var right = Math.Clamp(Math.Max(requested.Left, requested.Right), 0, materialMap.Width);
        var bottom = Math.Clamp(Math.Max(requested.Top, requested.Bottom), 0, materialMap.Height);
        return new RectI(left, top, right, bottom);
    }

    public static float SampleHeightAtMaterialCell(CoarseHeightMap heightMap, TerrainMaterialMap materialMap, int x, int y)
    {
        if (heightMap.Width <= 1 || heightMap.Height <= 1 || materialMap.Width <= 0 || materialMap.Height <= 0)
        {
            return 0f;
        }

        var hx = ((x + 0.5) / materialMap.Width) * (heightMap.Width - 1);
        var hy = ((y + 0.5) / materialMap.Height) * (heightMap.Height - 1);
        return TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy);
    }

    public static float EstimateLocalSlope(CoarseHeightMap heightMap, TerrainMaterialMap materialMap, int x, int y)
    {
        if (heightMap.Width <= 2 || heightMap.Height <= 2 || materialMap.Width <= 0 || materialMap.Height <= 0)
        {
            return 0f;
        }

        var hx = ((x + 0.5) / materialMap.Width) * (heightMap.Width - 1);
        var hy = ((y + 0.5) / materialMap.Height) * (heightMap.Height - 1);
        var dx = Math.Max(1.0 / Math.Max(1, materialMap.Width), (heightMap.Width - 1.0) / Math.Max(1, materialMap.Width));
        var dy = Math.Max(1.0 / Math.Max(1, materialMap.Height), (heightMap.Height - 1.0) / Math.Max(1, materialMap.Height));

        var hL = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx - dx, hy);
        var hR = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx + dx, hy);
        var hT = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy - dy);
        var hB = TerrainHeightEncoding.SampleCoarseHeightMapWorld(heightMap, hx, hy + dy);
        return MathF.Max(MathF.Abs(hR - hL), MathF.Abs(hB - hT));
    }


    public static bool IsNearWaterForMaterialCell(
        CoarseHeightMap heightMap,
        TerrainMaterialMap materialMap,
        int x,
        int y,
        float waterLineHeight,
        int coastBandCells = DefaultBeachCoastBandCells)
    {
        if (heightMap.Width <= 1 || heightMap.Height <= 1 || materialMap.Width <= 0 || materialMap.Height <= 0)
        {
            return true;
        }

        var radius = Math.Max(0, coastBandCells);
        var left = Math.Max(0, x - radius);
        var right = Math.Min(materialMap.Width - 1, x + radius);
        var top = Math.Max(0, y - radius);
        var bottom = Math.Min(materialMap.Height - 1, y + radius);
        var radiusSquared = radius * radius;

        for (var yy = top; yy <= bottom; yy++)
        {
            var dy = yy - y;
            for (var xx = left; xx <= right; xx++)
            {
                var dx = xx - x;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                if (SampleHeightAtMaterialCell(heightMap, materialMap, xx, yy) <= waterLineHeight)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int[] BuildWaterDistanceMap(CoarseHeightMap heightMap, TerrainMaterialMap materialMap, float waterLineHeight)
    {
        var width = materialMap.Width;
        var height = materialMap.Height;
        var distances = new int[Math.Max(0, width * height)];
        if (width <= 0 || height <= 0)
        {
            return distances;
        }

        const int far = 1_000_000;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                distances[index] = SampleHeightAtMaterialCell(heightMap, materialMap, x, y) <= waterLineHeight
                    ? 0
                    : far;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var best = distances[index];
                if (x > 0)
                {
                    best = Math.Min(best, distances[index - 1] + 1);
                }

                if (y > 0)
                {
                    best = Math.Min(best, distances[index - width] + 1);
                }

                distances[index] = best;
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var index = (y * width) + x;
                var best = distances[index];
                if (x + 1 < width)
                {
                    best = Math.Min(best, distances[index + 1] + 1);
                }

                if (y + 1 < height)
                {
                    best = Math.Min(best, distances[index + width] + 1);
                }

                distances[index] = best;
            }
        }

        return distances;
    }

    private static bool IsNearWater(IReadOnlyList<int> waterDistanceMap, int width, int x, int y, int coastBandCells)
    {
        var index = (y * width) + x;
        return index >= 0 && index < waterDistanceMap.Count && waterDistanceMap[index] <= Math.Max(0, coastBandCells);
    }

    private enum TerrainTransitionRole
    {
        None,
        DeepWater,
        ShallowWater,
        Beach,
        LowLand,
        Rock
    }

    private readonly record struct TerrainTransitionDecision(TerrainTransitionRole Role, int MaterialId);

    private static TerrainTransitionRole ClassifyMaterialRole(float worldHeight, float slope, bool isNearWater, TerrainCoastTransitionOptions options)
    {
        if (options.PaintRockOnSteepSlopes && worldHeight > options.BeachMaxHeight && slope >= options.RockSlopeThreshold && options.RockMaterialId >= 0)
        {
            return TerrainTransitionRole.Rock;
        }

        if (worldHeight <= options.DeepWaterMaxHeight)
        {
            if (!options.PaintDeepWater)
            {
                return TerrainTransitionRole.None;
            }

            return TerrainTransitionRole.DeepWater;
        }

        if (worldHeight <= options.WaterLineHeight)
        {
            if (!options.PaintShallowWater)
            {
                return TerrainTransitionRole.None;
            }

            return TerrainTransitionRole.ShallowWater;
        }

        if (worldHeight <= options.BeachMaxHeight && isNearWater)
        {
            if (!options.PaintBeach)
            {
                return TerrainTransitionRole.None;
            }

            return TerrainTransitionRole.Beach;
        }

        if (worldHeight <= options.LowLandMaxHeight)
        {
            if (!options.PaintLowLand)
            {
                return TerrainTransitionRole.None;
            }

            return TerrainTransitionRole.LowLand;
        }

        return TerrainTransitionRole.None;
    }

    private static void AddRoleStats(TerrainCoastTransitionStats stats, TerrainTransitionRole role)
    {
        switch (role)
        {
            case TerrainTransitionRole.DeepWater:
                stats.DeepWaterCells++;
                break;
            case TerrainTransitionRole.ShallowWater:
                stats.ShallowWaterCells++;
                break;
            case TerrainTransitionRole.Beach:
                stats.BeachCells++;
                break;
            case TerrainTransitionRole.LowLand:
                stats.LowLandCells++;
                break;
            case TerrainTransitionRole.Rock:
                stats.RockCells++;
                break;
        }
    }

    private static TerrainMaterialCellPattern? ResolveCustomPatternForRole(TerrainTransitionRole role, TerrainCoastTransitionOptions options)
    {
        return role switch
        {
            TerrainTransitionRole.DeepWater => options.DeepWaterCellPattern,
            TerrainTransitionRole.ShallowWater => options.ShallowWaterCellPattern,
            TerrainTransitionRole.Beach => options.BeachCellPattern,
            TerrainTransitionRole.LowLand => options.LowLandCellPattern,
            TerrainTransitionRole.Rock => options.RockCellPattern,
            _ => null
        };
    }

    private static int CountCustomTransitionPatterns(TerrainCoastTransitionOptions options)
    {
        var count = 0;
        if (options.DeepWaterCellPattern is not null) count++;
        if (options.ShallowWaterCellPattern is not null) count++;
        if (options.BeachCellPattern is not null) count++;
        if (options.LowLandCellPattern is not null) count++;
        if (options.RockCellPattern is not null) count++;
        return count;
    }

    private static int ResolveMaterialIdForRole(TerrainTransitionRole role, TerrainCoastTransitionOptions options)
    {
        return TerrainMaterialPackEncoding.ClampSlot(role switch
        {
            TerrainTransitionRole.DeepWater => options.DeepWaterMaterialId,
            TerrainTransitionRole.ShallowWater => options.ShallowWaterMaterialId,
            TerrainTransitionRole.Beach => options.BeachMaterialId,
            TerrainTransitionRole.LowLand => options.LowLandMaterialId,
            TerrainTransitionRole.Rock => options.RockMaterialId,
            _ => 0
        });
    }

    private static TerrainMaterialCellPattern BuildVanillaLikeTransitionPattern(TerrainTransitionRole role, int materialId, TerrainCoastTransitionOptions options, int materialBlockX, int materialBlockY)
    {
        return role switch
        {
            TerrainTransitionRole.DeepWater => CreatePattern(
                new[] { options.DeepWaterMaterialId, options.ShallowWaterMaterialId, options.BeachMaterialId, 3, 4, 1 },
                new[] { 31, 0, 0, 0, 0, 0 }),

            TerrainTransitionRole.ShallowWater => CreatePattern(
                new[] { options.ShallowWaterMaterialId, options.DeepWaterMaterialId, options.BeachMaterialId, 4, 3, 1 },
                new[] { 31, 8, 0, 0, 0, 0 }),

            TerrainTransitionRole.Beach => BuildBeachPattern(materialId),

            TerrainTransitionRole.LowLand => BuildLowLandGrassPattern(materialId, materialBlockX, materialBlockY),

            TerrainTransitionRole.Rock => CreatePattern(
                new[] { materialId, 30, 16, 15, 9, options.BeachMaterialId },
                new[] { 31, 16, 8, 0, 0, 0 }),

            _ => CreatePattern(
                new[] { materialId, 10, 7, 30, 16, 1 },
                new[] { 31, 0, 0, 0, 0, 0 })
        };
    }


    private static TerrainMaterialCellPattern BuildBeachPattern(int materialId)
    {
        var selected = TerrainMaterialPackEncoding.ClampSlot(materialId);
        if (selected == 31)
        {
            return CreatePattern(
                new[] { 31, 1, 23, 12, 20, 8 },
                new[] { 31, 16, 8, 0, 0, 0 });
        }

        if (selected == 23)
        {
            return CreatePattern(
                new[] { 23, 1, 31, 12, 20, 8 },
                new[] { 31, 16, 8, 0, 0, 0 });
        }

        return CreatePattern(
            new[] { selected, 23, 31, 12, 20, 8 },
            new[] { 31, 12, 6, 0, 0, 0 });
    }

    private static TerrainMaterialCellPattern BuildLowLandGrassPattern(int materialId, int materialBlockX, int materialBlockY)
    {
        // v44.3 : retour au mélange herbe v44.1, car la tentative v44.2 qui rendait l'ID
        // choisi dominant (souvent ID 2) produit un sol trop pâle/gris en vue proche.
        // Le problème restant n'est pas un excès de coverage 10/7/30, mais la répétition
        // d'une seule empreinte sur plusieurs centaines de milliers de texels. On garde donc
        // les couvertures fortes qui déclenchent correctement le bake/vegetation, tout en
        // variant l'ordre des matériaux par bloc MaterialIDs2 2x2 avec des combinaisons
        // vanilla-like observées sur terra moderate. La variation est déterministe et ne
        // touche ni aux tintmaps ni aux normalmaps.
        var selected = TerrainMaterialPackEncoding.ClampSlot(materialId);
        var variant = HashLowLandPatternVariant(materialBlockX, materialBlockY);

        if (selected == 10 || selected == 7 || selected == 30)
        {
            return variant switch
            {
                0 => CreatePattern(new[] { selected, 10, 7, 30, 1, 18 }, new[] { 31, 31, 31, 31, 0, 0 }),
                1 => CreatePattern(new[] { selected, 16, 10, 7, 30, 1 }, new[] { 31, 21, 31, 31, 31, 0 }),
                2 => CreatePattern(new[] { selected, 7, 30, 10, 1, 19 }, new[] { 31, 31, 31, 31, 0, 0 }),
                3 => CreatePattern(new[] { selected, 30, 10, 7, 16, 18 }, new[] { 31, 31, 31, 31, 0, 0 }),
                4 => CreatePattern(new[] { selected, 10, 30, 7, 1, 18 }, new[] { 31, 31, 31, 31, 0, 0 }),
                _ => CreatePattern(new[] { selected, 7, 10, 30, 1, 16 }, new[] { 31, 31, 31, 31, 0, 0 })
            };
        }

        // Cas par défaut : le combo UI sélectionne souvent ID 2 pour "terre basse".
        // En v44.1 cet ID restait dormant, ce qui rendait mieux en jeu que de le rendre
        // dominant. On conserve cette stratégie : 10/7/30 restent les splats visibles.
        return variant switch
        {
            0 => CreatePattern(new[] { 10, 7, 30, 1, selected, 19 }, new[] { 31, 31, 31, 31, 0, 0 }),
            1 => CreatePattern(new[] { 16, 10, 7, 30, 1, selected }, new[] { 21, 31, 31, 31, 31, 0 }),
            2 => CreatePattern(new[] { 10, 16, 7, 30, 1, selected }, new[] { 31, 21, 31, 31, 31, 0 }),
            3 => CreatePattern(new[] { 10, 7, 1, 30, 18, selected }, new[] { 31, 30, 31, 24, 0, 0 }),
            4 => CreatePattern(new[] { 30, 10, 7, 1, 18, selected }, new[] { 31, 31, 31, 0, 0, 0 }),
            _ => CreatePattern(new[] { 7, 10, 30, 1, 16, selected }, new[] { 31, 31, 31, 0, 0, 0 })
        };
    }

    private static int HashLowLandPatternVariant(int materialBlockX, int materialBlockY)
    {
        unchecked
        {
            var h = (uint)materialBlockX * 0x9E3779B1u;
            h ^= (uint)materialBlockY * 0x85EBCA77u;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            return (int)(h % 6u);
        }
    }

    private static TerrainMaterialCellPattern CreatePattern(IReadOnlyList<int> materialIds, IReadOnlyList<int> alphaWeights)
    {
        var ids = new int[TerrainMaterialPackEncoding.SlotCount];
        var alphas = new int[TerrainMaterialPackEncoding.SlotCount];
        for (var i = 0; i < TerrainMaterialPackEncoding.SlotCount; i++)
        {
            ids[i] = TerrainMaterialPackEncoding.ClampSlot(i < materialIds.Count ? materialIds[i] : 0);
            alphas[i] = TerrainMaterialPackEncoding.ClampSlot(i < alphaWeights.Count ? alphaWeights[i] : 0);
        }

        return new TerrainMaterialCellPattern
        {
            MaterialIds = ids,
            AlphaWeights = alphas
        };
    }
}
