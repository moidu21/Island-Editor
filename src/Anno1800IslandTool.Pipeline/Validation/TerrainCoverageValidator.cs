using System.Globalization;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Pipeline.Abstractions;

namespace Anno1800IslandTool.Pipeline.Validation;

/// <summary>
/// Validation de couverture terrain v30.
///
/// Contrairement aux versions précédentes, ce validateur ne compare plus les pages mesh et
/// normalmap par égalité stricte de clé (LOD/X/Y). Il projette chaque famille de pages sur la
/// coarse heightmap GameData et vérifie les dépendances par recouvrement spatial.
/// </summary>
public sealed class TerrainCoverageValidator : IValidator<IslandProject>
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();

        if (project.MeshPages.Count == 0 && project.NormalMapPages.Count == 0 && project.TintMapPages.Count == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.TerrainCoverageDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.T("Couverture terrain v30 : aucune page mesh/normalmap/tintmap importée."));
            return report;
        }

        var (sampleWidth, sampleHeight) = TerrainPageCoverage.ResolveReferenceSampleSize(project);
        if (sampleWidth <= 0 || sampleHeight <= 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.TerrainCoverageDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.T("Couverture terrain v30 : taille de référence GameData inconnue, recouvrement spatial impossible."));
            return report;
        }

        var coverage = TerrainPageCoverage.BuildProjectCoverage(project);
        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainCoverageDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Couverture terrain v30 : repère commun {sampleWidth}x{sampleHeight} samples GameData, validation par recouvrement spatial activée, LOD0/LOD1 protégés comme métadonnées/coverage et exclus des écritures de vertices."));

        AddFamilyCoverageSummary(report, coverage, TerrainPageCoverage.MeshFamily);
        AddFamilyCoverageSummary(report, coverage, TerrainPageCoverage.NormalMapFamily);
        AddFamilyCoverageSummary(report, coverage, TerrainPageCoverage.TintMapFamily);
        AddProtectedMeshLodDiagnostics(report, project, coverage);
        AddMeshNormalOverlapDiagnostics(report, project, coverage);
        AddTintOverlapDiagnostics(report, project, coverage);

        return report;
    }

    private static void AddFamilyCoverageSummary(ValidationReport report, IReadOnlyList<TerrainPageBounds> coverage, string family)
    {
        var pages = TerrainPageCoverage.GetFamily(coverage, family);
        if (pages.Count == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.TerrainCoverageDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.F($"{family} coverage : aucune page."));
            return;
        }

        var lodSummary = pages
            .GroupBy(p => p.Key.Lod)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var minX = g.Min(p => p.Key.X);
                var maxX = g.Max(p => p.Key.X);
                var minY = g.Min(p => p.Key.Y);
                var maxY = g.Max(p => p.Key.Y);
                var columns = g.Max(p => p.GridColumns);
                var rows = g.Max(p => p.GridRows);
                var exact = g.All(p => p.IsExactKeyGrid) ? "rect" : "crop";
                return Anno1800IslandTool.Core.Localization.TextCatalog.F($"L{g.Key}={g.Count()} [{minX}..{maxX}x{minY}..{maxY}] grille {columns}x{rows} {exact}");
            })
            .ToList();

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainCoverageDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"{family} coverage : {pages.Count} page(s), {string.Join("; ", lodSummary)}."));
    }

    private static void AddProtectedMeshLodDiagnostics(ValidationReport report, IslandProject project, IReadOnlyList<TerrainPageBounds> coverage)
    {
        var protectedPages = TerrainPageCoverage.GetFamily(coverage, TerrainPageCoverage.MeshFamily)
            .Where(p => MeshPageClassifier.IsProtectedMetadataLod(p.Key))
            .ToList();

        if (protectedPages.Count == 0)
        {
            return;
        }

        var withGroundObjects = protectedPages.Count(p => project.MeshPages.TryGetValue(p.Key, out var page) && MeshPageClassifier.HasGroundObjects(page));
        var withVertices = protectedPages.Count(p => project.MeshPages.TryGetValue(p.Key, out var page) && MeshPageClassifier.HasVertices(page));

        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainCoverageDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] Mesh LOD0/LOD1 : {protectedPages.Count} page(s) traitées comme métadonnées/coverage/ground objects, exclues de la validation mesh éditable. Ground objects sur {withGroundObjects}, vertices ignorés par sécurité sur {withVertices}. Aucune régénération de vertices n'est autorisée sur ces LOD sauf opération explicitement supportée."));
    }

    private static void AddMeshNormalOverlapDiagnostics(ValidationReport report, IslandProject project, IReadOnlyList<TerrainPageBounds> coverage)
    {
        var meshPages = TerrainPageCoverage.GetFamily(coverage, TerrainPageCoverage.MeshFamily)
            .Where(p => IsEditableOrProbableMesh(project, p.Key))
            .ToList();
        var normalPages = TerrainPageCoverage.GetFamily(coverage, TerrainPageCoverage.NormalMapFamily);

        if (meshPages.Count == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.TerrainCoverageDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.T("Mesh/NormalMap : aucune page mesh éditable/probable à vérifier."));
            return;
        }

        if (normalPages.Count == 0)
        {
            report.Add(ValidationSeverity.Warning, ValidationCode.MissingTerrainCounterpart, Anno1800IslandTool.Core.Localization.TextCatalog.T("Mesh/NormalMap : pages mesh éditables/probables présentes, mais aucune normalmap/heightpage importée."));
            return;
        }

        var missingSameLod = new List<TerrainPageBounds>();
        var coveredByOtherLodOnly = new List<TerrainPageBounds>();
        var totalCoveredSameLod = 0;

        foreach (var mesh in meshPages)
        {
            var sameLod = TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.NormalMapFamily, mesh.SampleBounds, mesh.Key.Lod);
            if (sameLod.Count > 0)
            {
                totalCoveredSameLod++;
                continue;
            }

            var anyLod = TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.NormalMapFamily, mesh.SampleBounds);
            if (anyLod.Count > 0)
            {
                coveredByOtherLodOnly.Add(mesh);
            }
            else
            {
                missingSameLod.Add(mesh);
            }
        }

        if (totalCoveredSameLod == meshPages.Count)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] Mesh/NormalMap : {totalCoveredSameLod}/{meshPages.Count} page(s) mesh éditables/probables ont au moins une normalmap au même LOD par recouvrement spatial."));
        }
        else
        {
            if (missingSameLod.Count > 0)
            {
                report.Add(
                    ValidationSeverity.Warning,
                    ValidationCode.MissingTerrainCounterpart,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh/NormalMap : {missingSameLod.Count}/{meshPages.Count} page(s) mesh éditables/probables n'ont aucune normalmap recouvrante, exemples : {FormatExamples(missingSameLod)}."));
            }

            if (coveredByOtherLodOnly.Count > 0)
            {
                report.Add(
                    ValidationSeverity.Info,
                    ValidationCode.TerrainCoverageDiagnostic,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh/NormalMap : {coveredByOtherLodOnly.Count} page(s) mesh n'ont pas de normalmap au même LOD, mais recoupent une normalmap d'un autre LOD, exemples : {FormatExamples(coveredByOtherLodOnly)}."));
            }

            if (totalCoveredSameLod > 0)
            {
                report.Add(
                    ValidationSeverity.Info,
                    ValidationCode.TerrainCoverageDiagnostic,
                    Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh/NormalMap : {totalCoveredSameLod}/{meshPages.Count} page(s) mesh éditables/probables couvertes au même LOD par recouvrement spatial."));
            }
        }

        var normalWithoutMeshSameLod = normalPages
            .Where(normal => !TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.MeshFamily, normal.SampleBounds, normal.Key.Lod).Any())
            .Take(24)
            .ToList();
        if (normalWithoutMeshSameLod.Count > 0)
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.TerrainCoverageDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"NormalMap : {normalWithoutMeshSameLod.Count} page(s) exemple sans mesh recouvrante au même LOD. Ce n'est pas forcément une erreur si la grille normalmap est plus large/recadrée : {FormatExamples(normalWithoutMeshSameLod)}."));
        }
    }

    private static void AddTintOverlapDiagnostics(ValidationReport report, IslandProject project, IReadOnlyList<TerrainPageBounds> coverage)
    {
        var meshPages = TerrainPageCoverage.GetFamily(coverage, TerrainPageCoverage.MeshFamily)
            .Where(p => IsEditableOrProbableMesh(project, p.Key))
            .ToList();
        var tintPages = TerrainPageCoverage.GetFamily(coverage, TerrainPageCoverage.TintMapFamily);

        if (meshPages.Count == 0 || tintPages.Count == 0)
        {
            return;
        }

        var covered = meshPages.Count(mesh => TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.TintMapFamily, mesh.SampleBounds).Any());
        report.Add(
            ValidationSeverity.Info,
            ValidationCode.TerrainCoverageDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"Mesh/TintMap : {covered}/{meshPages.Count} page(s) mesh éditables/probables recoupent au moins une tintmap, tous LOD confondus."));
    }

    private static bool IsEditableOrProbableMesh(IslandProject project, TerrainPageKey key)
    {
        project.MeshPages.TryGetValue(key, out var page);
        return MeshPageClassifier.IsEditableOrProbableGeometry(key, page);
    }

    private static string FormatExamples(IEnumerable<TerrainPageBounds> pages)
    {
        var values = pages
            .OrderBy(p => p.Key.Lod)
            .ThenBy(p => p.Key.X)
            .ThenBy(p => p.Key.Y)
            .Take(8)
            .Select(p => $"{p.Key} {p.SampleBounds}")
            .ToList();
        return values.Count == 0 ? Anno1800IslandTool.Core.Localization.TextCatalog.T("aucun") : string.Join(", ", values);
    }
}
