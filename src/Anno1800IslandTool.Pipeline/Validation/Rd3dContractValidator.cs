using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class Rd3dContractValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();
        var rd3d = project.Rd3d;

        ValidateValue(report, rd3d.NodeSize, 32, "NodeSize", project.Rd3dFilePath);
        ValidateValue(report, rd3d.PageSize, 512, "PageSize", project.Rd3dFilePath);
        ValidateValue(report, rd3d.TintPageSize, 512, "TintPageSize", project.Rd3dFilePath);
        ValidateValue(report, rd3d.NodesPerMeshCluster, 32, "NodesPerMeshCluster", project.Rd3dFilePath);
        ValidateValue(report, rd3d.MinMeshLevel, 2, "MinMeshLevel", project.Rd3dFilePath);
        ValidateValue(report, rd3d.HeightPageBorderTexels, 8, "HeightPageBorderTexels", project.Rd3dFilePath);

        if (rd3d.UnitScale != 0f && Math.Abs(rd3d.UnitScale - 0.03125f) > 0.0001f)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.SuspiciousRd3dSetting,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"UnitScale inattendu. Attendu 0.03125, obtenu {rd3d.UnitScale.ToString(CultureInfo.InvariantCulture)}."),
                project.Rd3dFilePath ?? "rd3d.xml");
        }

        if (!rd3d.UsingSplitImageHeightMaps && !string.IsNullOrWhiteSpace(project.Rd3dFilePath))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.SuspiciousRd3dSetting,
                Anno1800IslandTool.Core.Localization.TextCatalog.T("UsingSplitImageHeightMaps vaut False alors que les îles interprétées connues utilisent True."),
                project.Rd3dFilePath);
        }

        ValidateHasMeshArrayRowStride(report, project);
        ValidateLevelDimensions(report, project);

        return report;
    }

    private static void ValidateHasMeshArrayRowStride(ValidationReport report, IslandProject project)
    {
        var text = project.RawContent.Rd3dText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var matches = Regex.Matches(text!, @"<HasMeshArray>(?<body>.*?)</HasMeshArray>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var lod = 0; lod < matches.Count; lod++)
        {
            var body = matches[lod].Groups["body"].Value;
            var width = ReadIntTag(body, "x") ?? 0;
            var height = ReadIntTag(body, "y") ?? 0;
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            var bitCount = ReadIntListTag(body, "bits").Count;
            var expected = Align4((width + 7) / 8) * height;
            if (bitCount == expected)
            {
                report.Add(
                    ValidationSeverity.Info,
                    ValidationCode.TerrainCoverageDiagnostic,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] HasMeshArray LOD{lod} : {width}x{height}, {bitCount} octets, stride align4 valide."),
                    project.Rd3dFilePath ?? "rd3d.xml");
                continue;
            }

            report.Add(
                ValidationSeverity.Error,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"HasMeshArray LOD{lod} invalide : {bitCount} octets, attendu {expected} pour {width}x{height} avec stride align4(ceil(width/8)). Export à risque de dalles/culling/crash."),
                project.Rd3dFilePath ?? "rd3d.xml");
        }
    }

    private static int? ReadIntTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static void ValidateLevelDimensions(ValidationReport report, IslandProject project)
    {
        var text = project.RawContent.Rd3dText;
        if (string.IsNullOrWhiteSpace(text)) return;
        var levels = Regex.Match(text, @"<Levels>(?<body>.*?)</Levels>", RegexOptions.Singleline);
        var lod = 0;
        foreach (Match level in Regex.Matches(levels.Groups["body"].Value, @"<None>(?<body>.*?)</None>", RegexOptions.Singleline))
        {
            var body = level.Groups["body"].Value;
            var x = ReadIntTag(body, "LevelSizeX");
            var z = ReadIntTag(body, "LevelSizeZ");
            foreach (var tag in new[] { "MinYArray", "MaxYArray", "HasMeshArray" })
            {
                var array = Regex.Match(body, $@"<{tag}>(?<body>.*?)</{tag}>", RegexOptions.Singleline).Groups["body"].Value;
                var w = ReadIntTag(array, tag == "HasMeshArray" ? "x" : "width");
                var h = ReadIntTag(array, tag == "HasMeshArray" ? "y" : "height");
                if ((x.HasValue && w.HasValue && x != w) || (z.HasValue && h.HasValue && z != h))
                    report.Add(ValidationSeverity.Error, ValidationCode.TerrainCoverageDiagnostic,
                        Anno1800IslandTool.Core.Localization.TextCatalog.F($"LOD{lod} : LevelSize {x}x{z} différent de {tag} {w}x{h}."), project.Rd3dFilePath);
            }
            var step = ReadIntTag(body, "NodeSizeWS");
            if (step > 0 && x.HasValue && z.HasValue && project.Rd3d.GridWidth > 0 && project.Rd3d.GridHeight > 0)
            {
                var expectedX = (int)Math.Ceiling(project.Rd3d.GridWidth / (32.0 * step.Value));
                var expectedZ = (int)Math.Ceiling(project.Rd3d.GridHeight / (32.0 * step.Value));
                if (x != expectedX || z != expectedZ)
                    report.Add(ValidationSeverity.Error, ValidationCode.TerrainCoverageDiagnostic,
                        Anno1800IslandTool.Core.Localization.TextCatalog.F($"LOD{lod} : LevelSize {x}x{z}, attendu {expectedX}x{expectedZ} pour NodeSizeWS={step}."), project.Rd3dFilePath);
            }
            lod++;
        }
    }

    private static List<int> ReadIntListTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*(?<values>[^<]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return new List<int>();
        }

        return Regex.Matches(match.Groups["values"].Value, @"-?\d+", RegexOptions.CultureInvariant)
            .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static int Align4(int value)
        => value <= 0 ? 0 : ((value + 3) / 4) * 4;

    private static void ValidateValue(ValidationReport report, int value, int expected, string label, string? relatedFile)
    {
        if (value != 0 && value != expected)
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.SuspiciousRd3dSetting,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"{label} inattendu. Attendu {expected}, obtenu {value}."),
                relatedFile ?? "rd3d.xml");
        }
    }
}
