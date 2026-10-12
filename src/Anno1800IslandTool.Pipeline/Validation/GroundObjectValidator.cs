using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Validation;

namespace Anno1800IslandTool.Pipeline.Validation;

public sealed class GroundObjectValidator
{
    public ValidationReport Validate(IslandProject project)
    {
        var report = new ValidationReport();
        var all = project.MeshPages.Values
            .SelectMany(page => page.Clusters.SelectMany((cluster, clusterIndex) => cluster.GroundObjects.Select(obj => new
            {
                Page = page,
                ClusterIndex = clusterIndex,
                Object = obj
            })))
            .ToList();

        if (all.Count == 0)
        {
            report.Add(ValidationSeverity.Info, ValidationCode.GroundObjectDiagnostic, Anno1800IslandTool.Core.Localization.TextCatalog.T("[Info] GroundObjects : aucun objet de sol/prop/végétation décodé dans les pages mesh chargées. Coche la lecture détaillée des pages pour les éditer."));
            return report;
        }

        var byKind = all.GroupBy(x => x.Object.Kind).ToDictionary(g => g.Key, g => g.Count());
        var lowLod = all.Count(x => x.Page.Key.Lod <= 1);
        var pages = all.Select(x => x.Page.Key).Distinct().Count();
        report.Add(
            ValidationSeverity.Info,
            ValidationCode.GroundObjectDiagnostic,
            Anno1800IslandTool.Core.Localization.TextCatalog.F($"[OK] GroundObjects : {all.Count} objet(s) sur {pages} page(s) mesh. Vegetation={CountKind(byKind, GroundObjectKind.Vegetation)}, Prop={CountKind(byKind, GroundObjectKind.Prop)}, Decoration={CountKind(byKind, GroundObjectKind.Decoration)}, Unknown={CountKind(byKind, GroundObjectKind.Unknown)}, LOD0/LOD1={lowLod}."));

        foreach (var item in all.Where(x => string.IsNullOrWhiteSpace(x.Object.ObjectFileName)).Take(12))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.GroundObjectInvalid,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"GroundObject sans FileName sur LOD {item.Page.Key.Lod}, page {item.Page.Key.X}x{item.Page.Key.Y}, cluster {item.ClusterIndex}."),
                item.Page.OriginalRelativePath);
        }

        var extent = Math.Max(1, project.Metadata.MapSize);
        foreach (var item in all.Where(x => x.Object.PositionXZ.X < -1 || x.Object.PositionXZ.Y < -1 || x.Object.PositionXZ.X > extent + 1 || x.Object.PositionXZ.Y > extent + 1).Take(12))
        {
            report.Add(
                ValidationSeverity.Warning,
                ValidationCode.GroundObjectInvalid,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"GroundObject hors étendue approximative de l'île : {item.Object.ObjectFileName} à {item.Object.PositionXZ.X:0.###}, {item.Object.PositionXZ.Y:0.###} pour MapSize={extent}."),
                item.Page.OriginalRelativePath);
        }

        foreach (var page in project.MeshPages.Values.Where(p => p.Key.Lod <= 1 && p.HasEditablePayload && p.Clusters.Any(c => c.GroundObjects.Count > 0)).Take(12))
        {
            report.Add(
                ValidationSeverity.Info,
                ValidationCode.GroundObjectDiagnostic,
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"[Info] Page LOD{page.Key.Lod} avec GroundObjects et vertices : conservée comme page metadata/coverage/ground objects, sans édition vertex."),
                page.OriginalRelativePath);
        }

        return report;
    }

    private static int CountKind(IReadOnlyDictionary<GroundObjectKind, int> counts, GroundObjectKind kind)
        => counts.TryGetValue(kind, out var value) ? value : 0;
}
