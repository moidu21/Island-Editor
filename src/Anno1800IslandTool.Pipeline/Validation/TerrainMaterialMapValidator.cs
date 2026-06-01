using System.Globalization;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

/// <summary>
/// Validation v30 du packing MaterialIDs2 / MaterialAlphas2.
///
/// Points importants confirmés sur les îles vanilla :
/// - MaterialIDs2 est partagé par bloc MaterialIDBlockSize x MaterialIDBlockSize.
/// - MaterialAlphas2 est stocké par texel.
/// - Les 6 alphas sont des coverages indépendants 0..31 ; leur somme peut dépasser 31.
/// </summary>
public sealed class TerrainMaterialMapValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();
        var map = project.Rd3d.MaterialMap;
        var relatedFile = project.Rd3dFilePath ?? "rd3d.xml";

        if (project.Rd3d.MaterialMapWidth <= 0 || project.Rd3d.MaterialMapHeight <= 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.MaterialMapEncoding, "MaterialMap v30 : dimensions RD3D absentes, diagnostic MaterialIDs2/MaterialAlphas2 ignoré.", relatedFile);
            return report;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            map.Width = project.Rd3d.MaterialMapWidth;
            map.Height = project.Rd3d.MaterialMapHeight;
        }

        if (map.MaterialIdBlockSize <= 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MaterialMapEncoding, $"MaterialMap v30 : MaterialIDBlockSize invalide ({map.MaterialIdBlockSize}). La valeur attendue observée est 2.", relatedFile);
            return report;
        }

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.MaterialMapEncoding,
            $"MaterialMap v30 : {map.Width}x{map.Height}, block size {map.MaterialIdBlockSize}, packing {TerrainMaterialPackEncoding.SlotCount} slots x {TerrainMaterialPackEncoding.BitsPerSlot} bits. Les sommes MaterialAlphas2 >31 sont acceptées et préservées.",
            relatedFile);

        ValidateBlockShape(report, map, relatedFile);
        ValidateCounts(report, map, relatedFile);
        ValidateHighBits(report, "MaterialIDs2", map.PackedMaterialIds, relatedFile);
        ValidateHighBits(report, "MaterialAlphas2", map.PackedMaterialAlphas, relatedFile);
        AddAlphaCoverageSummary(report, map, relatedFile);
        ValidateMaterialIdsAgainstMaterialSet(report, project, map, relatedFile);
        AddDominantMaterialSummary(report, map, relatedFile);

        return report;
    }

    private static void ValidateBlockShape(ValidationReport report, TerrainMaterialMap map, string relatedFile)
    {
        if (map.Width % map.MaterialIdBlockSize != 0 || map.Height % map.MaterialIdBlockSize != 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.MaterialMapEncoding,
                $"MaterialMap v30 : {map.Width}x{map.Height} n'est pas divisible proprement par MaterialIDBlockSize={map.MaterialIdBlockSize}. Le validateur utilisera une grille de blocs arrondie, mais ce cas est atypique pour les îles vanilla.",
                relatedFile);
            return;
        }

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.MaterialMapEncoding,
            $"[OK] MaterialMap v30 : grille MaterialIDs2 attendue {map.BlockWidth}x{map.BlockHeight} bloc(s).",
            relatedFile);
    }

    private static void ValidateCounts(ValidationReport report, TerrainMaterialMap map, string relatedFile)
    {
        if (map.PackedMaterialIds.Length == 0 && map.PackedMaterialAlphas.Length == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.MaterialMapEncoding, "MaterialMap v30 : MaterialIDs2 et MaterialAlphas2 absents ou non lus dans rd3d.xml.", relatedFile);
            return;
        }

        if (map.PackedMaterialIds.Length == map.ExpectedPackedMaterialIdCount)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.MaterialMapEncoding, $"[OK] MaterialIDs2 : {map.PackedMaterialIds.Length} bloc(s) packés, attendu {map.ExpectedPackedMaterialIdCount}.", relatedFile);
        }
        else
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MaterialMapEncoding, $"MaterialIDs2 : {map.PackedMaterialIds.Length} bloc(s) lus, attendu {map.ExpectedPackedMaterialIdCount} pour {map.Width}x{map.Height} avec block size {map.MaterialIdBlockSize}.", relatedFile);
        }

        if (map.PackedMaterialAlphas.Length == map.ExpectedPackedAlphaCount)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.MaterialMapEncoding, $"[OK] MaterialAlphas2 : {map.PackedMaterialAlphas.Length} texel(s) packés, attendu {map.ExpectedPackedAlphaCount}.", relatedFile);
        }
        else
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MaterialMapEncoding, $"MaterialAlphas2 : {map.PackedMaterialAlphas.Length} texel(s) lus, attendu {map.ExpectedPackedAlphaCount} pour {map.Width}x{map.Height}.", relatedFile);
        }
    }

    private static void ValidateHighBits(ValidationReport report, string label, IReadOnlyList<uint> values, string relatedFile)
    {
        if (values.Count == 0)
        {
            return;
        }

        var bad = values.Select((value, index) => new { value, index }).Where(x => !TerrainMaterialPackEncoding.IsValidPackedSix5Bit(x.value)).Take(8).ToList();
        if (bad.Count == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.MaterialMapEncoding, $"[OK] {label} : bits inutilisés 30..31 à zéro sur les valeurs lues.", relatedFile);
            return;
        }

        report.Add(
            ValidationSeverity.Warning,
            ValidationCode.MaterialMapEncoding,
            $"{label} : certaines valeurs utilisent les bits 30..31, atypique pour un packing 6x5 bits. Exemples index=valeur : {string.Join(", ", bad.Select(x => $"{x.index}={x.value}"))}.",
            relatedFile);
    }

    private static void AddAlphaCoverageSummary(ValidationReport report, TerrainMaterialMap map, string relatedFile)
    {
        if (map.PackedMaterialAlphas.Length == 0)
        {
            return;
        }

        var minSum = int.MaxValue;
        var maxSum = int.MinValue;
        var zeroTexels = 0;
        var over31Texels = 0;
        long totalSum = 0;
        var activeSlotHistogram = new int[TerrainMaterialPackEncoding.SlotCount + 1];
        var examples = new List<string>();

        for (var i = 0; i < map.PackedMaterialAlphas.Length; i++)
        {
            var alphas = TerrainMaterialPackEncoding.DecodeMaterialAlphas(map.PackedMaterialAlphas[i]);
            var sum = alphas.Sum();
            var activeSlots = alphas.Count(a => a > 0);
            minSum = Math.Min(minSum, sum);
            maxSum = Math.Max(maxSum, sum);
            totalSum += sum;
            activeSlotHistogram[Math.Clamp(activeSlots, 0, TerrainMaterialPackEncoding.SlotCount)]++;

            if (sum == 0)
            {
                zeroTexels++;
            }

            if (sum > TerrainMaterialPackEncoding.MaxPackedSlotValue)
            {
                over31Texels++;
                if (examples.Count < 6)
                {
                    examples.Add($"#{i}=sum{sum}[{string.Join("/", alphas)}]");
                }
            }
        }

        if (map.PackedMaterialAlphas.Length == 0)
        {
            return;
        }

        var avg = totalSum / (double)map.PackedMaterialAlphas.Length;
        var histogram = string.Join(", ", activeSlotHistogram
            .Select((count, slotCount) => new { slotCount, count })
            .Where(x => x.count > 0)
            .Select(x => $"{x.slotCount}slot={x.count}"));

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.MaterialMapEncoding,
            $"MaterialAlphas2 v30 : somme slots min/avg/max {minSum}/{avg.ToString("0.##", CultureInfo.InvariantCulture)}/{maxSum}, texels zéro={zeroTexels}, histogramme actifs [{histogram}].",
            relatedFile);

        if (zeroTexels > 0)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.MaterialMapEncoding,
                $"MaterialAlphas2 : {zeroTexels} texel(s) ont une somme de coverage égale à zéro. Cela peut provoquer des dalles blanches/grises dans le shader terrain. La v43.7 corrige ces texels avant export/peinture.",
                relatedFile);
        }

        if (over31Texels > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.MaterialMapEncoding,
                $"[OK] MaterialAlphas2 v30 : {over31Texels} texel(s) ont une somme >31. C'est accepté : la v30 ne normalise pas destructivement ces coverages. Exemples : {string.Join(", ", examples)}.",
                relatedFile);
        }
    }

    private static void ValidateMaterialIdsAgainstMaterialSet(ValidationReport report, IslandProject project, TerrainMaterialMap map, string relatedFile)
    {
        if (map.PackedMaterialIds.Length == 0)
        {
            return;
        }

        if (project.MaterialSet is null || project.MaterialSet.Materials.Count == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.MaterialMapEncoding,
                "MaterialIDs2 : materialset effectif absent/non chargé ; vérification des IDs inconnus ignorée.",
                relatedFile);
            return;
        }

        var known = project.MaterialSet.Materials.Select(m => m.Id).ToHashSet();
        var usedSlots = CountMaterialIdSlotOccurrences(map);
        var coveredUsage = CountCoveredMaterialUsage(map);

        var unknownCovered = coveredUsage.Keys
            .Where(id => id != 0 && id != TerrainMaterialPackEncoding.MaxPackedSlotValue && !known.Contains(id))
            .OrderBy(id => id)
            .ToList();

        var unknownDormant = usedSlots.Keys
            .Where(id => id != 0 && id != TerrainMaterialPackEncoding.MaxPackedSlotValue && !known.Contains(id) && !coveredUsage.ContainsKey(id))
            .OrderBy(id => id)
            .ToList();

        if (unknownCovered.Count == 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.MaterialMapEncoding,
                $"[OK] MaterialIDs2 : {usedSlots.Count} ID(s) présents dans les slots, aucun ID inconnu avec alpha >0. IDs réservés 0/31 tolérés.",
                relatedFile);
        }
        else
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.MaterialMapEncoding,
                $"MaterialIDs2 v30 : ID(s) absents du materialset effectif mais utilisés avec alpha >0 : {FormatIdUsage(unknownCovered, coveredUsage)}. Warning seulement : les îles vanilla peuvent contenir des slots réservés/fallback, l'export n'est pas bloqué.",
                relatedFile);
        }

        if (unknownDormant.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.MaterialMapEncoding,
                $"MaterialIDs2 : ID(s) absents du materialset mais sans coverage alpha détecté : {string.Join(", ", unknownDormant.Take(24))}{(unknownDormant.Count > 24 ? "..." : string.Empty)}. Signalé en info car ces slots semblent dormants.",
                relatedFile);
        }
    }

    private static Dictionary<int, int> CountMaterialIdSlotOccurrences(TerrainMaterialMap map)
    {
        var usage = new Dictionary<int, int>();
        foreach (var packed in map.PackedMaterialIds)
        {
            foreach (var id in TerrainMaterialPackEncoding.DecodeMaterialIds(packed))
            {
                usage[id] = usage.TryGetValue(id, out var current) ? current + 1 : 1;
            }
        }

        return usage;
    }

    private static Dictionary<int, long> CountCoveredMaterialUsage(TerrainMaterialMap map)
    {
        var usage = new Dictionary<int, long>();
        if (map.Width <= 0 || map.Height <= 0 || map.MaterialIdBlockSize <= 0 ||
            map.PackedMaterialIds.Length != map.ExpectedPackedMaterialIdCount ||
            map.PackedMaterialAlphas.Length != map.ExpectedPackedAlphaCount)
        {
            return usage;
        }

        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var blockIndex = ((y / map.MaterialIdBlockSize) * map.BlockWidth) + (x / map.MaterialIdBlockSize);
                var alphaIndex = (y * map.Width) + x;
                var ids = TerrainMaterialPackEncoding.DecodeMaterialIds(map.PackedMaterialIds[blockIndex]);
                var alphas = TerrainMaterialPackEncoding.DecodeMaterialAlphas(map.PackedMaterialAlphas[alphaIndex]);
                for (var slot = 0; slot < TerrainMaterialPackEncoding.SlotCount; slot++)
                {
                    var alpha = alphas[slot];
                    if (alpha <= 0)
                    {
                        continue;
                    }

                    var id = ids[slot];
                    usage[id] = usage.TryGetValue(id, out var current) ? current + alpha : alpha;
                }
            }
        }

        return usage;
    }

    private static string FormatIdUsage(IReadOnlyList<int> ids, IReadOnlyDictionary<int, long> usage)
        => string.Join(", ", ids.Take(16).Select(id => $"{id}(alphaSum={usage[id]})")) + (ids.Count > 16 ? "..." : string.Empty);

    private static void AddDominantMaterialSummary(ValidationReport report, TerrainMaterialMap map, string relatedFile)
    {
        if (map.PackedMaterialIds.Length == 0 || map.PackedMaterialAlphas.Length == 0 || map.Width <= 0 || map.Height <= 0 || map.PackedMaterialIds.Length != map.ExpectedPackedMaterialIdCount || map.PackedMaterialAlphas.Length != map.ExpectedPackedAlphaCount)
        {
            return;
        }

        var top = map.CountDominantMaterials(maxSamples: 8192)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(8)
            .Select(pair => $"ID {pair.Key}={pair.Value}")
            .ToList();

        if (top.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.MaterialMapEncoding,
                $"MaterialMap v30 : matériaux dominants échantillonnés ({top.Count} principaux) : {string.Join(", ", top)}.",
                relatedFile);
        }
    }
}
